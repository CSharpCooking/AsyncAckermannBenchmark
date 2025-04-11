using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.ObjectPool;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

// Run benchmarks for the Ackermann class
BenchmarkRunner.Run<Ackermann>();

[IterationCount(100)]        // Run 100 iterations per benchmark
[MemoryDiagnoser]            // Enable memory allocation diagnostics
public class Ackermann
{
    // AckermannFunc(3,3) = 61, total recursive calls = 2432
    [Params(1, 2, 3)]        // Parameter for testing different values of n
    public int n;

    [Params(1, 2, 3)]        // Parameter for testing different values of m
    public int m;

    // Baseline synchronous implementation of Ackermann function
    [Benchmark(Baseline = true)]
    public int Baseline()
    {
        return AckermannFunc(m, n);
        // Local function implementing Ackermann function
        int AckermannFunc(int m, int n) => (m, n) switch
        {
            (0, _) => n + 1,                     // Case 1
            (_, 0) => AckermannFunc(m - 1, 1),    // Case 2
            _ => AckermannFunc(m - 1, AckermannFunc(m, n - 1)), // Case 3
        };
    }

    // ValueTask-based asynchronous implementation
    [Benchmark]
    public ValueTask<int> ValueTask()
    {
        return AckermannFunc(m, n);
        // Async local function using ValueTask
        async ValueTask<int> AckermannFunc(int m, int n) => (m, n) switch
        {
            (0, _) => n + 1,
            (_, 0) => await AckermannFunc(m - 1, 1),
            _ => await AckermannFunc(m - 1, await AckermannFunc(m, n - 1)),
        };
    }

    // IValueTaskSource-based optimized implementation
    [Benchmark]
    public ValueTask<int> IValueTaskSource()
    {
        // Use pooled operation objects for better performance
        return AckermannOperation.Rent(m, n).StartOperationAsync();
    }

    // Task-based asynchronous implementation
    [Benchmark]
    public Task<int> Task()
    {
        return AckermannFunc(m, n);
        // Async local function using Task
        async Task<int> AckermannFunc(int m, int n) => (m, n) switch
        {
            (0, _) => n + 1,
            (_, 0) => await AckermannFunc(m - 1, 1),
            _ => await AckermannFunc(m - 1, await AckermannFunc(m, n - 1)),
        };
    }
}

// Custom IValueTaskSource implementation for Ackermann function
public class AckermannOperation : IValueTaskSource<int>, IDisposable
{
    // Thread-local pool of operation objects to avoid allocations
    private static readonly ThreadLocal<ObjectPool<AckermannOperation>> _threadLocalPool =
        new(() => new DefaultObjectPool<AckermannOperation>(new DefaultPooledObjectPolicy<AckermannOperation>()));

    private static ObjectPool<AckermannOperation> _pool => _threadLocalPool.Value;

    // Core implementation for ValueTask source
    private ManualResetValueTaskSourceCore<int> _core;
    private int _result;     // Stores the operation result
    private int _m;          // Current m parameter
    private int _n;          // Current n parameter

    public AckermannOperation() { }

    // Rent an operation object from the pool
    public static AckermannOperation Rent(int m, int n)
    {
        var operation = _pool.Get();
        operation._m = m;
        operation._n = n;
        operation._core.Reset();
        return operation;
    }

    // Start the asynchronous operation
    public ValueTask<int> StartOperationAsync()
    {
        if (_m == 0)
        {
            // Case 1: m = 0
            _result = _n + 1;
            _core.SetResult(_result);
        }
        else if (_n == 0)
        {
            // Case 2: n = 0
            var nextOp = Rent(_m - 1, 1);
            var token = nextOp._core.Version;

            // Register continuation for the next operation
            nextOp.OnCompleted(
                state =>
                {
                    var op = (AckermannOperation)state;
                    _result = op.GetResult(token);   // Get result from next operation
                    _core.SetResult(_result);        // Set our result
                    op.Dispose();                    // Return nextOp to pool
                },
                nextOp,
                token,
                ValueTaskSourceOnCompletedFlags.None);

            nextOp.StartOperationAsync();
        }
        else
        {
            // Case 3: both m > 0 and n > 0
            var firstOp = Rent(_m, _n - 1);
            var firstToken = firstOp._core.Version;

            // Register continuation for first operation
            firstOp.OnCompleted(
                state =>
                {
                    var op1 = (AckermannOperation)state;
                    var intermediateResult = op1.GetResult(firstToken);

                    // Start second operation with intermediate result
                    var secondOp = Rent(_m - 1, intermediateResult);
                    var secondToken = secondOp._core.Version;

                    secondOp.OnCompleted(
                        innerState =>
                        {
                            var op2 = (AckermannOperation)innerState;
                            _result = op2.GetResult(secondToken);
                            _core.SetResult(_result);  // Set final result
                            op1.Dispose();            // Return firstOp to pool
                            op2.Dispose();            // Return secondOp to pool
                        },
                        secondOp,
                        secondToken,
                        ValueTaskSourceOnCompletedFlags.None);

                    secondOp.StartOperationAsync();
                },
                firstOp,
                firstToken,
                ValueTaskSourceOnCompletedFlags.None);

            firstOp.StartOperationAsync();
        }

        return new ValueTask<int>(this, _core.Version);
    }

    // IValueTaskSource implementation
    public int GetResult(short token) => _core.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
    public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    // Return this instance to the pool
    public void Dispose()
    {
        _pool.Return(this);
    }
}