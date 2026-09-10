using System.IO;
using System.Text.Json;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Ruleset;
using ClassIsland.Shared;
using Microsoft.Extensions.Hosting;

namespace Copper_Bulb.Automation;

/// <summary>
    /// 铜灯条件服务：不自行轮询宿主，启动路径零阻塞。
    /// 铜灯状态的边沿翻转与持久化统一在规则处理程序 <see cref="Handle"/> 中完成，
    /// 利用宿主每次对承载规则集的求值驱动边沿检测，因此任意场景
    /// （组件隐藏条件、窗口规则、自动化工作流等）都能按 T 触发器语义工作。
    /// 持久化状态在后台线程读取；读取完成后由 <see cref="SweepRestoredState"/>
    /// 一次性纠正已注册的活对象并广播，保证重启后铜灯尽快生效。
    /// 状态变化通过一次性防抖定时器延迟后台落盘并广播，避免求值期间写盘或重入。
    /// </summary>
    public class CopperBulbService : IHostedService
{
    /// <summary>
    /// "铜灯"规则的 ID。
    /// </summary>
    public const string RuleId = "copper_bulb.bulb";

    /// <summary>
    /// 服务单例，供规则处理程序与设置界面访问。
    /// </summary>
    public static CopperBulbService? Instance { get; private set; }

    /// <summary>
    /// 插件私有配置目录（由 PluginBase.PluginConfigFolder 注入），
    /// 用于持久化铜灯状态，与铜灯所在宿主容器无关。
    /// </summary>
    public static string? PluginConfigFolder { get; set; }

    private readonly Dictionary<Guid, CopperBulbRuleSettings> _bulbs = new();
    private readonly object _bulbsLock = new();
    private readonly Dictionary<string, PersistedState> _store = new();
    private readonly object _storeLock = new();
    private volatile bool _storeLoaded;
    private bool _storeDirty;
    private bool _needNotify;
    private Task _saveChain = Task.CompletedTask;
    private System.Threading.Timer? _publishTimer; // 后台线程防抖计时器
    private readonly object _publishLock = new();
    private IRulesetService? _rulesetService;
    private int _restoreDone; // 0=未广播，1=已广播；保证“恢复后广播”整体恰好一次
    [ThreadStatic] private static int _evalDepth;

    /// <summary>
    /// 在宿主调用线程上的最大内联递归深度。超过该深度后，铜灯内层规则集的求值
    /// 会离栈切换到大栈后台线程执行，避免深嵌套时在调用线程上发生不可捕获的
    /// StackOverflowException（进程直接崩溃）。
    /// </summary>
    private const int MaxInlineDepth = 8;

    private static readonly BigStackEvaluator LazyBigStack = new();

    private string? StorePath =>
        string.IsNullOrEmpty(PluginConfigFolder)
            ? null
            : Path.Combine(PluginConfigFolder, "copper_bulb_state.json");

    /// <summary>
    /// 持久化的铜灯状态（以 Id 的字符串为键）。
    /// </summary>
    private sealed class PersistedState
    {
        public bool IsOn { get; set; }
        public bool? LastInnerState { get; set; }
    }

    /// <summary>
    /// 专用大栈求值线程：当铜灯嵌套较深时，把内层规则集求值切换到该线程执行。
    /// 线程栈（64MB）足以容纳大量嵌套层级，从而把“深递归”从小的调用线程栈上挪走。
    /// 同一时刻串行处理请求；仅在求值期间阻塞发起方，语义与直接调用等价。
    /// </summary>
    private sealed class BigStackEvaluator
    {
        private readonly object _sync = new();
        private readonly Queue<(Func<bool> Job, TaskCompletionSource<bool> Tcs)> _queue = new();
        private readonly Thread _worker;

        public BigStackEvaluator()
        {
            _worker = new Thread(Loop, 64 * 1024 * 1024)
            {
                IsBackground = true,
                Name = "CopperBulbEval"
            };
            _worker.Start();
        }

        /// <summary>
        /// 在调用线程上阻塞执行 <paramref name="job"/>。若调用方本身就是大栈线程
        /// （说明正处于深嵌套递归中），则直接内联执行，避免重新排队造成死锁。
        /// </summary>
        public bool Run(Func<bool> job)
        {
            if (Thread.CurrentThread == _worker)
                return job(); // 已在大栈线程上，直接递归（本线程栈足够大）

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _queue.Enqueue((job, tcs));
                Monitor.Pulse(_sync);
            }
            return tcs.Task.GetAwaiter().GetResult();
        }

        private void Loop()
        {
            for (;;)
            {
                (Func<bool> Job, TaskCompletionSource<bool> Tcs) item;
                lock (_sync)
                {
                    while (_queue.Count == 0)
                        Monitor.Wait(_sync);
                    item = _queue.Dequeue();
                }

                bool result;
                try
                {
                    result = item.Job();
                }
                catch
                {
                    result = false;
                }
                item.Tcs.TrySetResult(result);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Instance = this;
        // 不在启动时解析任何宿主服务，也不做同步 I/O；
        // 持久化状态在后台线程加载，加载完成后由 SweepRestoredState 统一纠正并广播。
        Task.Run(LoadStore);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_publishLock)
        {
            _publishTimer?.Dispose();
            _publishTimer = null;
        }

        QueueSave(); // 兜底：关闭前把尚未落盘的状态写盘
        _saveChain.GetAwaiter().GetResult(); // 等待后台写入链完成，保证数据落盘
        return Task.CompletedTask;
    }

    private void LoadStore()
    {
        if (_storeLoaded) return;
        var path = StorePath;
        if (path == null || !File.Exists(path))
        {
            _storeLoaded = true;
            return;
        }

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, PersistedState>>(File.ReadAllText(path));
            lock (_storeLock)
            {
                if (data != null)
                {
                    // 合并而非清空：磁盘值优先（覆盖加载窗口期内由宿主旧副本写入的条目），
                    // 但保留窗口期内新建铜灯的条目。
                    foreach (var kv in data) _store[kv.Key] = kv.Value;
                }
            }
        }
        catch
        {
            // 损坏则忽略，从头开始。
        }
        finally
        {
            _storeLoaded = true;
        }

        SweepRestoredState();
    }

    /// <summary>
    /// 后台加载完成后的一次性纠正：把持久化状态应用到已注册的活对象上，
    /// 并总是排定一次兜底广播。兜底广播不依赖 Handle 是否被调用——即使宿主尚未求值任何
    /// 铜灯，广播也会促使宿主重估相关规则集并回调 <see cref="Handle"/>，
    /// 从而确保“启动后立即生效”不取决于宿主首位求值时机。
    /// </summary>
    private void SweepRestoredState()
    {
        lock (_bulbsLock)
        {
            lock (_storeLock)
            {
                foreach (var live in _bulbs.Values)
                {
                    if (_store.TryGetValue(live.Id.ToString(), out var st)
                        && (live.IsOn != st.IsOn || live.LastInnerState != st.LastInnerState))
                    {
                        live.IsOn = st.IsOn;
                        live.LastInnerState = st.LastInnerState;
                    }
                }
            }
        }

        // 延迟给宿主组件完成订阅留足时间；由 _restoreDone 保证整体恰好一次。
        EnsureRestoreBroadcast(800);
    }

    /// <summary>
    /// 确保“重启后恢复状态”被广播恰好一次（原子）。
    /// 计时与状态处理均在后台线程完成，仅真正发通知时才投递到 UI 线程。
    /// </summary>
    private void EnsureRestoreBroadcast(int delayMs)
    {
        if (Interlocked.CompareExchange(ref _restoreDone, 1, 0) != 0)
            return; // 本会话已广播过恢复，跳过

        lock (_publishLock)
        {
            _publishTimer ??= new System.Threading.Timer(OnPublish, null, Timeout.Infinite, Timeout.Infinite);
        }

        _needNotify = true;
        _publishTimer!.Change(Math.Max(delayMs, 1), Timeout.Infinite);
    }

    /// <summary>
    /// 把当前存储的快照排入后台写入链，按入队顺序串行落盘，不阻塞调用线程。
    /// </summary>
    private void QueueSave()
    {
        var path = StorePath;
        if (path == null) return;
        Dictionary<string, PersistedState> copy;
        lock (_storeLock)
        {
            copy = new Dictionary<string, PersistedState>(_store);
        }

        var chain = _saveChain;
        _saveChain = chain.ContinueWith(
            _ => WriteStore(path, copy),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void WriteStore(string path, Dictionary<string, PersistedState> copy)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 原子写入：先写入同目录下的临时文件，再原子替换目标文件，
            // 避免写入中途系统崩溃导致目标文件损坏/缺失数据。
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(copy));
            File.Move(tmpPath, path, true);
        }
        catch
        {
            // 落盘失败不影响内存状态；下次脏标记触发时重试。
        }
    }

    /// <summary>
    /// 取（或注册）某个 Id 对应的稳定活对象，供任意规则集场景复用锁存状态。
    /// 首次遇到时把插件持久化的状态覆盖到活对象上，保证重启后延续。
    /// </summary>
    private CopperBulbRuleSettings GetOrAddLive(CopperBulbRuleSettings s)
    {
        lock (_bulbsLock)
        {
            if (!_bulbs.TryGetValue(s.Id, out var live))
            {
                live = s;
                _bulbs[s.Id] = live;
            }

            // 以插件自持状态为准，覆盖宿主配置里可能过期的副本。
            lock (_storeLock)
            {
                if (_store.TryGetValue(live.Id.ToString(), out var st))
                {
                    live.IsOn = st.IsOn;
                    live.LastInnerState = st.LastInnerState;
                }
            }

            return live;
        }
    }

    /// <summary>
    /// "铜灯"规则处理程序：每次宿主求值承载它的规则集时会被调用——
    /// 先求值内部规则集（同时刷新内部条件的状态指示点），
    /// 再检测内部条件“不满足→满足”的上升沿并翻转点亮状态，
    /// 最后返回铜灯当前点亮状态。
    /// </summary>
    public static bool Handle(object? settings)
    {
        if (settings is not CopperBulbRuleSettings s) return false;
        if (++_evalDepth > 64) // 防御极端嵌套/自引用
        {
            _evalDepth--;
            return s.IsOn;
        }
        try
        {
            var inst = Instance;
            if (inst == null) return s.IsOn;

            // 取活对象；若持久化状态已加载则直接应用（未加载时由后台加载完成后的
            // SweepRestoredState 统一纠正，这里绝不等待、不阻塞）。
            var live = inst.GetOrAddLive(s);

            // 重启恢复广播：Handle 被调用说明宿主正在求值该规则集、承载元件必定在监听，
            // 此时若已加载且活对象（已恢复持久化状态）与宿主旧副本不一致，立即广播让元件重估。
            // 未加载/无差异时不在此广播——由 SweepRestoredState 完成后的兜底延迟广播覆盖，
            // 因此不存在“宿主迟迟不求值导致铜灯永不生效”的空窗。
            if (inst._storeLoaded && live.IsOn != s.IsOn)
                inst.EnsureRestoreBroadcast(150); // 较短延迟，立即生效

            var rs = inst._rulesetService ??= IAppHost.TryGetService<IRulesetService>();
            if (rs == null)
                return live.IsOn;

            // 深嵌套时离栈到大栈线程求值内层规则集，避免在调用线程上递归过深导致
            // 不可捕获的 StackOverflowException（进程崩溃）。浅嵌套时仍直接内联，
            // 不给常见场景增加线程切换开销。
            var inner = _evalDepth > MaxInlineDepth
                ? LazyBigStack.Run(() => rs.IsRulesetSatisfied(live.InternalRuleset))
                : rs.IsRulesetSatisfied(live.InternalRuleset);

            var flipped = false;
            if (live.LastInnerState == null)
            {
                live.LastInnerState = inner; // 首次只建立基线，不翻转
            }
            else
            {
                var prev = live.LastInnerState.Value;
                live.LastInnerState = inner;
                if (!prev && inner) // 上升沿：不满足→满足
                {
                    live.IsOn = !live.IsOn;
                    flipped = true;
                }
            }

            var changed = inst.PersistControllerState(live);
            if (changed || flipped)
                inst.SchedulePublish(flipped);

            return live.IsOn;
        }
        finally
        {
            _evalDepth--;
        }
    }

    /// <summary>
    /// 把某个铜灯的当前状态写回插件持久化存储；若状态确实发生变化返回 true。
    /// </summary>
    private bool PersistControllerState(CopperBulbRuleSettings live)
    {
        if (!_storeLoaded)
            return false; // 后台加载完成前不落盘，避免用宿主旧值覆盖磁盘上的正确状态

        var key = live.Id.ToString();
        lock (_storeLock)
        {
            if (!_store.TryGetValue(key, out var st))
            {
                _store[key] = new PersistedState { IsOn = live.IsOn, LastInnerState = live.LastInnerState };
                return true;
            }

            var changed = st.IsOn != live.IsOn || st.LastInnerState != live.LastInnerState;
            st.IsOn = live.IsOn;
            st.LastInnerState = live.LastInnerState;
            return changed;
        }
    }

    /// <summary>
    /// 状态变化后安排一次后台线程“落盘 + 广播”的一口径防抖处理，避免在元件求值期间写盘/重入。
    /// 计时与落盘均在后台线程完成；仅需要通知宿主时才投递一个 UI 分派调用。
    /// </summary>
    private void SchedulePublish(bool notify)
    {
        if (notify)
            _needNotify = true;
        _storeDirty = true;

        lock (_publishLock)
        {
            _publishTimer ??= new System.Threading.Timer(OnPublish, null, Timeout.Infinite, Timeout.Infinite);
            _publishTimer!.Change(150, Timeout.Infinite);
        }
    }

    private void OnPublish(object? state)
    {
        var notify = _needNotify;
        _needNotify = false;

        if (_storeDirty)
        {
            _storeDirty = false;
            QueueSave(); // 后台线程写盘，不阻塞 UI
        }

        if (notify)
            Dispatcher.UIThread.Post(() =>
            {
                var rs = _rulesetService ??= IAppHost.TryGetService<IRulesetService>();
                try
                {
                    rs?.NotifyStatusChanged(); // 让宿主相关元件重估并应用新的铜灯状态
                }
                catch
                {
                    // 广播失败不影响状态本身。
                }
            });
    }
}