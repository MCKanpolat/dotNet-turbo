﻿using Qoollo.Turbo.Threading.ServiceStuff;
using Qoollo.Turbo.Threading.ThreadPools.ServiceStuff;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Debug = System.Diagnostics.Debug;

namespace Qoollo.Turbo.Threading.ThreadPools.Common
{
    /// <summary>
    /// Shared logic for ThreadPools
    /// </summary>
    public abstract class CommonThreadPool : ThreadPoolBase, ICustomSynchronizationContextSupplier, IContextSwitchSupplier
    {
        /// <summary>
        /// SynchronizationContext implementation for CommonThreadPool
        /// </summary>
        private class CommonThreadPoolSynchronizationContext : SynchronizationContext
        {
            private readonly CommonThreadPool _srcPool;

            /// <summary>
            /// <see cref="CommonThreadPoolSynchronizationContext"/> constructor
            /// </summary>
            /// <param name="srcPool">Owner</param>
            public CommonThreadPoolSynchronizationContext(CommonThreadPool srcPool)
            {
                TurboContract.Requires(srcPool != null, conditionString: "srcPool != null");
                _srcPool = srcPool;
            }

            /// <summary>
            /// Creates a copy of the synchronization context
            /// </summary>
            /// <returns>Created copy of SynchronizationContext</returns>
            public override SynchronizationContext CreateCopy()
            {
                return new CommonThreadPoolSynchronizationContext(_srcPool);
            }
            /// <summary>
            /// Dispatches an asynchronous message to a synchronization context
            /// </summary>
            /// <param name="d">Delegate to be executed</param>
            /// <param name="state">The object passed to the delegate</param>
            public override void Post(SendOrPostCallback d, object state)
            {
                var item = new SendOrPostCallbackThreadPoolWorkItem(d, state, true, false);
                _srcPool.AddWorkItem(item);
            }
            /// <summary>
            /// Dispatches a synchronous message to a synchronization context
            /// </summary>
            /// <param name="d">Delegate to be executed</param>
            /// <param name="state">The object passed to the delegate</param>
            public override void Send(SendOrPostCallback d, object state)
            {
                var item = new SendOrPostCallbackSyncThreadPoolWorkItem(d, state, true, true);
                _srcPool.AddWorkItem(item);
                item.Wait();
            }
        }

        // =====================

        /// <summary>
        /// TaskScheduler implementation for CommonThreadPool
        /// </summary>
        private class CommonThreadPoolTaskScheduler: TaskScheduler
        {
            private readonly CommonThreadPool _srcPool;
            /// <summary>
            /// <see cref="CommonThreadPoolTaskScheduler"/> constructor
            /// </summary>
            /// <param name="srcPool">Owner</param>
            public CommonThreadPoolTaskScheduler(CommonThreadPool srcPool)
            {
                TurboContract.Requires(srcPool != null, conditionString: "srcPool != null");
                _srcPool = srcPool;
            }
            /// <summary>
            /// Returns the list of tasks in queue for debug purposes
            /// </summary>
            protected override IEnumerable<Task> GetScheduledTasks()
            {
                return null;
            }
            /// <summary>
            /// Adds task to the ThreadPool queue
            /// </summary>
            /// <param name="task">Task</param>
            protected override void QueueTask(Task task)
            {
                ThreadPoolWorkItem poolItem = task.AsyncState as ThreadPoolWorkItem;
                TurboContract.Assert(poolItem == null || 
                                    (poolItem.GetType().GetTypeInfo().IsGenericType && 
                                    (poolItem.GetType().GetGenericTypeDefinition() == typeof(TaskEntryExecutionWithClosureThreadPoolWorkItem<>) || 
                                     poolItem.GetType().GetGenericTypeDefinition() == typeof(TaskEntryExecutionWithClosureThreadPoolWorkItem<,>))), conditionString: "poolItem == null || \r\n                                    (poolItem.GetType().IsGenericType && \r\n                                    (poolItem.GetType().GetGenericTypeDefinition() == typeof(TaskEntryExecutionWithClosureThreadPoolWorkItem<>) || \r\n                                     poolItem.GetType().GetGenericTypeDefinition() == typeof(TaskEntryExecutionWithClosureThreadPoolWorkItem<,>)))");

                if (poolItem != null)
                    _srcPool.AddWorkItem(poolItem);
                else
                    _srcPool.AddWorkItem(new TaskEntryExecutionThreadPoolWorkItem(task, false));
            }
            /// <summary>
            /// Attempts to exectue Task synchronously in the current thread (successful only if the current thread is from ThreadPool)
            /// </summary>
            /// <param name="task">Task to be executed</param>
            /// <param name="taskWasPreviouslyQueued">Task was taken from queue</param>
            /// <returns>True if the task can be executed synchronously in the current thread</returns>
            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                return !taskWasPreviouslyQueued && _srcPool.IsThreadPoolThread && base.TryExecuteTask(task);
            }
        }


        // =====================

        /// <summary>
        /// Local data for every ThreadPool thread
        /// </summary>
        protected class ThreadPrivateData
        {
            internal ThreadPrivateData(ThreadPoolThreadLocals localData)
            {
                TurboContract.Requires(localData != null, conditionString: "localData != null");
                LocalData = localData;
            }

            /// <summary>
            /// Local data for thread
            /// </summary>
            internal readonly ThreadPoolThreadLocals LocalData;
        }

        // =====================


        /// <summary>
        /// All data necessary to perform initial initialization within a newly created thread
        /// </summary>
        private class ThreadStartUpData
        {
            public ThreadStartUpData(Action<ThreadPrivateData, CancellationToken> threadProc, ThreadStartControllingToken startController, bool createThreadLocalQueue)
            {
                TurboContract.Requires(threadProc != null, conditionString: "threadProc != null");
                TurboContract.Requires(startController != null, conditionString: "startController != null");

                ThreadProcedure = threadProc;
                StartController = startController;
                CreateThreadLocalQueue = createThreadLocalQueue;
            }

            public readonly Action<ThreadPrivateData, CancellationToken> ThreadProcedure;
            public readonly ThreadStartControllingToken StartController;
            public readonly bool CreateThreadLocalQueue;
        }


        // =====================


        // Number of processor cores
        private static readonly int ProcessorCount = Environment.ProcessorCount;

        // =============

        private readonly bool _isBackgroundThreads;

        private readonly string _name;
        private int _currentThreadNum;


        /// <summary>
        /// List of all threads in ThreadPool
        /// </summary>
        private readonly List<Thread> _activeThread;
        private int _activeThreadCount;


        private readonly bool _restoreExecutionContext;
        private readonly bool _useOwnSyncContext;
        private readonly bool _useOwnTaskScheduler;
        private readonly CommonThreadPoolSynchronizationContext _synchroContext;
        private readonly CommonThreadPoolTaskScheduler _taskScheduler;

        /// <summary>
        /// Global data for ThreadPool (global and local queues, ThreadStatic fields)
        /// </summary>
        private readonly ThreadPoolGlobals _threadPoolGlobals;

        private int _threadPoolState;
        private readonly CancellationTokenSource _threadRunningCancellation;

        private readonly ManualResetEventSlim _stopWaiter;
        private volatile bool _letFinishProcess;
        private volatile bool _completeAdding;


        /// <summary>
        /// <see cref="CommonThreadPool"/> constructor
        /// </summary>
        /// <param name="queueBoundedCapacity">The bounded size of the work items queue (if less or equal to 0 then no limitation)</param>
        /// <param name="queueStealAwakePeriod">Periods of sleep between checking for the possibility to steal a work item from other local queues</param>
        /// <param name="isBackground">Whether or not threads are a background threads</param>
        /// <param name="name">The name for this instance of ThreadPool and for its threads</param>
        /// <param name="useOwnTaskScheduler">Whether or not set ThreadPool TaskScheduler as a default for all ThreadPool threads</param>
        /// <param name="useOwnSyncContext">Whether or not set ThreadPool SynchronizationContext as a default for all ThreadPool threads</param>
        /// <param name="flowExecutionContext">Whether or not to flow ExecutionContext to the ThreadPool thread</param>
        public CommonThreadPool(int queueBoundedCapacity, int queueStealAwakePeriod, bool isBackground, string name, bool useOwnTaskScheduler, bool useOwnSyncContext, bool flowExecutionContext)
        {
            _isBackgroundThreads = isBackground;
            _name = name ?? this.GetType().GetCSName();
            _restoreExecutionContext = flowExecutionContext;
            _useOwnSyncContext = useOwnSyncContext;
            _useOwnTaskScheduler = useOwnTaskScheduler;

            _synchroContext = new CommonThreadPoolSynchronizationContext(this);
            _taskScheduler = new CommonThreadPoolTaskScheduler(this);

            _threadPoolGlobals = new ThreadPoolGlobals(queueBoundedCapacity, queueStealAwakePeriod, _name);
            _activeThread = new List<Thread>(Math.Min(100, Math.Max(2, ProcessorCount)));
            _activeThreadCount = 0;

            _threadPoolState = (int)ThreadPoolState.Created;
            _threadRunningCancellation = new CancellationTokenSource();
            _stopWaiter = new ManualResetEventSlim(false);
            _letFinishProcess = false;
            _completeAdding = false;

            Turbo.Profiling.Profiler.ThreadPoolCreated(Name);
        }


        /// <summary>
        /// The name for this instance of ThreadPool
        /// </summary>
        public string Name
        {
            get { return _name; }
        }
        /// <summary>
        /// Gets value indicating whether or not thread pool threads are a background threads
        /// </summary>
        public bool IsBackgroundThreads
        {
            get { return _isBackgroundThreads; }
        }
        /// <summary>
        /// Number of active threads
        /// </summary>
        public int ThreadCount
        {
            get { return Volatile.Read(ref _activeThreadCount); }
        }
        /// <summary>
        /// The bounded size of the work items queue (if less or equal to 0 then no limitation)
        /// </summary>
        public int QueueCapacity
        {
            get { return _threadPoolGlobals.GlobalQueue.BoundedCapacity; }
        }
        /// <summary>
        /// Extended capacity of the work items queue
        /// </summary>
        protected int ExtendedQueueCapacity
        {
            get { return _threadPoolGlobals.GlobalQueue.ExtendedCapacity; }
        }
        /// <summary>
        /// Number of items in global work items queue
        /// </summary>
        public int GlobalQueueWorkItemCount
        {
            get { return _threadPoolGlobals.GlobalQueue.OccupiedNodesCount; }
        }
        /// <summary>
        /// Is ThreadPool marked as Completed for Adding
        /// </summary>
        public bool IsAddingCompleted
        {
            get { return _completeAdding; }
        }
        /// <summary>
        /// Whether the user specified that all existed work items should be processed before stop
        /// </summary>
        protected bool LetFinishedProcess
        {
            get { return _letFinishProcess; }
        }
        /// <summary>
        /// Whether the stop was requested or already stopped
        /// </summary>
        protected bool IsStopRequestedOrStopped
        {
            get
            {
                var state = State;
                return state == ThreadPoolState.Stopped || state == ThreadPoolState.StopRequested;
            }
        }
        /// <summary>
        /// Whether the ThreadPool is running and can process work items
        /// </summary>
        public bool IsWork
        {
            get
            {
                var state = State;
                return state == ThreadPoolState.Running || state == ThreadPoolState.Created;
            }
        }
        /// <summary>
        /// Gets a value indicating whether or not a thread belongs to the current thread pool
        /// </summary>
        public bool IsThreadPoolThread
        {
            get { return _threadPoolGlobals.IsThreadPoolThread; }
        }
        /// <summary>
        /// Synchronization context for the current ThreadPool
        /// </summary>
        protected SynchronizationContext SynchronizationContext
        {
            get { return _synchroContext; }
        }
        /// <summary>
        /// Task scheduler associated with the current ThreadPool
        /// </summary>
        public TaskScheduler TaskScheduler
        {
            get { return _taskScheduler; }
        }

        /// <summary>
        /// Current state
        /// </summary>
        public ThreadPoolState State
        {
            get { return (ThreadPoolState)Volatile.Read(ref _threadPoolState); }
        }
        /// <summary>
        /// Verifies that state transition is possible
        /// </summary>
        /// <param name="oldState">Current state</param>
        /// <param name="newState">New state</param>
        /// <returns>True when state transition can be performed</returns>
        private bool IsValidStateTransition(ThreadPoolState oldState, ThreadPoolState newState)
        {
            switch (oldState)
            {
                case ThreadPoolState.Created:
                    return newState == ThreadPoolState.Running || newState == ThreadPoolState.StopRequested || newState == ThreadPoolState.Stopped;
                case ThreadPoolState.Running:
                    return newState == ThreadPoolState.StopRequested;
                case ThreadPoolState.StopRequested:
                    return newState == ThreadPoolState.Stopped;
                case ThreadPoolState.Stopped:
                    return false;
                default:
                    return false;
            }
        }
        /// <summary>
        /// Safely changes the current state
        /// </summary>
        /// <param name="newState">New state</param>
        /// <param name="prevState">Previously observed state</param>
        /// <returns>Was state changed (false means that the state transition is not valid)</returns>
        private bool ChangeStateSafe(ThreadPoolState newState, out ThreadPoolState prevState)
        {
            prevState = (ThreadPoolState)Volatile.Read(ref _threadPoolState);

            if (!IsValidStateTransition(prevState, newState))
                return false;

            SpinWait sw = new SpinWait();
            while (Interlocked.CompareExchange(ref _threadPoolState, (int)newState, (int)prevState) != (int)prevState)
            {
                sw.SpinOnce();
                prevState = (ThreadPoolState)Volatile.Read(ref _threadPoolState);
                if (!IsValidStateTransition(prevState, newState))
                    return false;
            }

            return true;
        }
        /// <summary>
        /// Handles state chaning to <see cref="ThreadPoolState.Stopped"/>
        /// </summary>
        private void OnGoToStoppedState()
        {
            TurboContract.Assert(State == ThreadPoolState.Stopped, conditionString: "State == ThreadPoolState.Stopped");

            TurboContract.Assert(_stopWaiter.IsSet == false, conditionString: "_stopWaiter.IsSet == false");
            _stopWaiter.Set();
            _threadPoolGlobals.Dispose();
            Profiling.Profiler.ThreadPoolDisposed(Name, false);
        }


        /// <summary>
        /// Enqueues a method for exection inside the current ThreadPool
        /// </summary>
        /// <param name="action">Representing the method to execute</param>
        /// <param name="preferFairness">When true places work item directly to the global queue, otherwise can place work item to the thread local queue</param>
        /// <exception cref="ArgumentNullException">Action is null</exception>
        public void Run(Action action, bool preferFairness)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            AddWorkItem(new ActionThreadPoolWorkItem(action, true, preferFairness));
        }
        /// <summary>
        /// Attempts to enqueue a method for exection inside the current ThreadPool
        /// </summary>
        /// <param name="action">Representing the method to execute</param>
        /// <param name="preferFairness">When true places work item directly to the global queue, otherwise can place work item to the thread local queue</param>
        /// <returns>True if work item was added to the queue, otherwise false</returns>
        /// <exception cref="ArgumentNullException">Action is null</exception>
        public bool TryRun(Action action, bool preferFairness)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            return TryAddWorkItem(new ActionThreadPoolWorkItem(action, true, preferFairness));
        }

        /// <summary>
        /// Enqueues a method for exection inside the current ThreadPool
        /// </summary>
        /// <typeparam name="T">Type of the user state object</typeparam>
        /// <param name="action">Representing the method to execute</param>
        /// <param name="state">State object</param>
        /// <param name="preferFairness">When true places work item directly to the global queue, otherwise can place work item to the thread local queue</param>
        /// <exception cref="ArgumentNullException">Action is null</exception>
        public void Run<T>(Action<T> action, T state, bool preferFairness)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            AddWorkItem(new ActionThreadPoolWorkItem<T>(action, state, true, preferFairness));
        }

        /// <summary>
        /// Attempts to enqueue a method for exection inside the current ThreadPool
        /// </summary>
        /// <typeparam name="T">Type of the user state object</typeparam>
        /// <param name="action">Representing the method to execute</param>
        /// <param name="state">State object</param>
        /// <param name="preferFairness">When true places work item directly to the global queue, otherwise can place work item to the thread local queue</param>
        /// <returns>True if work item was added to the queue, otherwise false</returns>
        /// <exception cref="ArgumentNullException">Action is null</exception>
        public bool TryRun<T>(Action<T> action, T state, bool preferFairness)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            return TryAddWorkItem(new ActionThreadPoolWorkItem<T>(action, state, true, preferFairness));
        }


        /// <summary>
        /// Enqueues a method for exection inside the current ThreadPool and returns a <see cref="Task"/> that represents queued operation
        /// </summary>
        /// <param name="action">Representing the method to execute</param>
        /// <returns>Create Task</returns>
        /// <exception cref="ArgumentNullException">Action is null</exception>
        public sealed override Task RunAsTask(Action action)
        {
            return this.RunAsTask(action, TaskCreationOptions.None);
        }
        /// <summary>
        /// Enqueues a method for exection inside the current ThreadPool and returns a <see cref="Task"/> that represents queued operation
        /// </summary>
        /// <param name="action">Representing the method to execute</param>
        /// <param name="creationOptions">Task creation options</param>
        /// <returns>Created Task</returns>
        /// <exception cref="ArgumentNullException">Action is null</exception>
        public Task RunAsTask(Action action, TaskCreationOptions creationOptions)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            if (_useOwnTaskScheduler)
            {
                var result = new Task(action, creationOptions);
                result.Start(_taskScheduler);
                return result;
            }
            else
            {
                var item = new TaskThreadPoolWorkItem(action, creationOptions);
                AddWorkItem(item);
                return item.Task;
            }
        }

        /// <summary>
        /// Enqueues a method for exection inside the current ThreadPool and returns a <see cref="Task"/> that represents queued operation
        /// </summary>
        /// <typeparam name="TState">Type of the user state object</typeparam>
        /// <param name="action">Representing the method to execute</param>
        /// <param name="state">State object</param>
        /// <returns>Created Task</returns>
        /// <exception cref="ArgumentNullException">Action is null</exception>
        public sealed override Task RunAsTask<TState>(Action<TState> action, TState state)
        {
            return this.RunAsTask(action, state, TaskCreationOptions.None);
        }
        /// <summary>
        /// Enqueues a method for exection inside the current ThreadPool and returns a <see cref="Task"/> that represents queued operation
        /// </summary>
        /// <typeparam name="TState">Type of the user state object</typeparam>
        /// <param name="action">Representing the method to execute</param>
        /// <param name="state">State object</param>
        /// <param name="creationOptions">Task creation options</param>
        /// <returns>Created Task</returns>
        /// <exception cref="ArgumentNullException">Action is null</exception>
        public Task RunAsTask<TState>(Action<TState> action, TState state, TaskCreationOptions creationOptions)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            if (_useOwnTaskScheduler)
            {
                var item = new TaskEntryExecutionWithClosureThreadPoolWorkItem<TState>(action, state, creationOptions);
                var result = new Task(TaskEntryExecutionWithClosureThreadPoolWorkItem<TState>.RunRawAction, item, creationOptions);
                item.SetTask(result);
                result.Start(_taskScheduler);
                return result;
            }
            else
            {
                var item = new TaskThreadPoolWorkItem<TState>(action, state, creationOptions);
                AddWorkItem(item);
                return item.Task;
            }
        }


        /// <summary>
        /// Enqueues a method for exection inside the current ThreadPool and returns a <see cref="Task{TRes}"/> that represents queued operation
        /// </summary>
        /// <typeparam name="TRes">The type of the operation result</typeparam>
        /// <param name="func">Representing the method to execute</param>
        /// <returns>Created Task</returns>
        /// <exception cref="ArgumentNullException">Func is null</exception>
        public sealed override Task<TRes> RunAsTask<TRes>(Func<TRes> func)
        {
            return this.RunAsTask<TRes>(func, TaskCreationOptions.None);
        }
        /// <summary>
        /// Enqueues a method for exection inside the current ThreadPool and returns a <see cref="Task{TRes}"/> that represents queued operation
        /// </summary>
        /// <typeparam name="TRes">The type of the operation result</typeparam>
        /// <param name="func">Representing the method to execute</param>
        /// <param name="creationOptions">Task creation options</param>
        /// <returns>Created Task</returns>
        /// <exception cref="ArgumentNullException">Func is null</exception>
        public Task<TRes> RunAsTask<TRes>(Func<TRes> func, TaskCreationOptions creationOptions)
        {
            if (func == null)
                throw new ArgumentNullException(nameof(func));

            if (_useOwnTaskScheduler)
            {
                var result = new Task<TRes>(func, creationOptions);
                result.Start(_taskScheduler);
                return result;
            }
            else
            {
                var item = new TaskFuncThreadPoolWorkItem<TRes>(func, creationOptions);
                AddWorkItem(item);
                return item.Task;
            }
        }

        /// <summary>
        /// Enqueues a method for exection inside the current ThreadPool and returns a <see cref="Task{TRes}"/> that represents queued operation
        /// </summary>
        /// <typeparam name="TState">Type of the user state object</typeparam>
        /// <typeparam name="TRes">The type of the operation result</typeparam>
        /// <param name="func">Representing the method to execute</param>
        /// <param name="state">State object</param>
        /// <returns>Created Task</returns>
        /// <exception cref="ArgumentNullException">Func is null</exception>
        public sealed override Task<TRes> RunAsTask<TState, TRes>(Func<TState, TRes> func, TState state)
        {
            return this.RunAsTask<TState, TRes>(func, state, TaskCreationOptions.None);
        }
        /// <summary>
        /// Enqueues a method for exection inside the current ThreadPool and returns a <see cref="Task{TRes}"/> that represents queued operation
        /// </summary>
        /// <typeparam name="TState">Type of the user state object</typeparam>
        /// <typeparam name="TRes">The type of the operation result</typeparam>
        /// <param name="func">Representing the method to execute</param>
        /// <param name="state">State object</param>
        /// <param name="creationOptions">Task creation options</param>
        /// <returns>Created Task</returns>
        /// <exception cref="ArgumentNullException">Func is null</exception>
        public Task<TRes> RunAsTask<TState, TRes>(Func<TState, TRes> func, TState state, TaskCreationOptions creationOptions)
        {
            if (func == null)
                throw new ArgumentNullException(nameof(func));

            if (_useOwnTaskScheduler)
            {
                var item = new TaskEntryExecutionWithClosureThreadPoolWorkItem<TState, TRes>(func, state, creationOptions);
                var result = new Task<TRes>(TaskEntryExecutionWithClosureThreadPoolWorkItem<TState, TRes>.RunRawAction, item, creationOptions);
                item.SetTask(result);
                result.Start(_taskScheduler);
                return result;
            }
            else
            {
                var item = new TaskFuncThreadPoolWorkItem<TState, TRes>(func, state, creationOptions);
                AddWorkItem(item);
                return item.Task;
            }
        }

        /// <summary>
        /// Creates an awaitable that asynchronously switch to the current ThreadPool when awaited
        /// </summary>
        /// <returns>Context switch awaitable object</returns>
        public sealed override ContextSwitchAwaitable SwitchToPool()
        {
            if (IsThreadPoolThread)
                return new ContextSwitchAwaitable();

            return new ContextSwitchAwaitable(this);
        }


        /// <summary>
        /// Blocks the current thread and waits for all processing threads to complete
        /// </summary>
        public void WaitUntilStop()
        {
            if (State == ThreadPoolState.Stopped)
                return;
            _stopWaiter.Wait();
            TurboContract.Assert(State == ThreadPoolState.Stopped, conditionString: "State == ThreadPoolState.Stopped");
        }

        /// <summary>
        /// Blocks the current thread and waits for all processing threads to complete
        /// </summary>
        /// <param name="timeout">Waiting timeout in milliseconds</param>
        /// <returns>True when all threads completed in time</returns>
        public bool WaitUntilStop(int timeout)
        {
            if (State == ThreadPoolState.Stopped)
                return true;
            return _stopWaiter.Wait(timeout);
        }

        /// <summary>
        /// Checks whether the current instance is in Stopped state and throws ObjectDisposedException when it is
        /// </summary>
        protected void CheckDisposed()
        {
            if (State == ThreadPoolState.Stopped)
                throw new ObjectDisposedException(this.GetType().Name, "ThreadPool is Stopped");
        }
        /// <summary>
        /// Checks whether the current instance is in Stopped or StopRequested state and throws ObjectDisposedException when it is
        /// </summary>
        protected void CheckPendingDisposeOrDisposed()
        {
            var state = State;
            if (state == ThreadPoolState.Stopped || state == ThreadPoolState.StopRequested)
                throw new ObjectDisposedException(this.GetType().Name, "ThreadPool has Stopped or StopRequested state");
        }


        /// <summary>
        /// Scans all threads and gets the information about their states
        /// </summary>
        /// <param name="totalThreadCount">Total number of threads</param>
        /// <param name="runningCount">Number of running threads (in <see cref="ThreadState.Running"/> state)</param>
        /// <param name="waitingCount">Number of threads in <see cref="ThreadState.WaitSleepJoin"/> state</param>
        protected void ScanThreadStates(out int totalThreadCount, out int runningCount, out int waitingCount)
        {
            totalThreadCount = 0;
            runningCount = 0;
            waitingCount = 0;

            lock (_activeThread)
            {
                for (int i = 0; i < _activeThread.Count; i++)
                {
                    var curState = _activeThread[i].ThreadState;
                    if ((curState & ~ThreadState.Background) == ThreadState.Running)
                        runningCount++;
                    if ((curState & ThreadState.WaitSleepJoin) != 0)
                        waitingCount++;
                }
                totalThreadCount = _activeThread.Count;
            }
        }

        /// <summary>
        /// Gets the CancellationToken that will be cancelled when a stop is requested 
        /// </summary>
        /// <returns>Cancellation token</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected CancellationToken GetThreadRunningCancellationToken()
        {
            return _threadRunningCancellation.Token;
        }

        /// <summary>
        /// Rethrows the exception stored inside Task, when it is in Cancelled or Faulted state
        /// </summary>
        /// <param name="task">Task</param>
        private void RethrowTaskException(Task task)
        {
            TurboContract.Requires(task != null, conditionString: "task != null");

            if (task.IsCanceled)
                throw new TaskCanceledException("ThreadPool task was cancelled by unknown reason. ThreadPool: " + this.Name);

            if (task.IsFaulted)
            {
                var exception = task.Exception;
                if (exception == null)
                    throw new ThreadPoolException("ThreadPool task was faulted by unknown reason. ThreadPool: " + this.Name);
                if (exception.InnerExceptions.Count != 1)
                    throw exception;

                var capturedException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException);
                capturedException.Throw();
            }
        }

        /// <summary>
        /// Thread start-up procedure. The first procedure that executed within the thread. 
        /// Here the initial initialization is performed
        /// </summary>
        /// <param name="rawData">Start-up data (instance of <see cref="ThreadStartUpData"/>)</param>
        [System.Diagnostics.DebuggerNonUserCode]
        private void ThreadStartUpProc(object rawData)
        {
            TurboContract.Requires(rawData != null, conditionString: "rawData != null");

            ThreadStartUpData startupData = (ThreadStartUpData)rawData;

            SpinWait sw = new SpinWait();
            while (!startupData.StartController.IsInitialized)
                sw.SpinOnce();

            if (!startupData.StartController.IsOk)
                return;

            if (_useOwnSyncContext)
                SynchronizationContext.SetSynchronizationContext(_synchroContext);

            try
            {
                Profiling.Profiler.ThreadPoolThreadCountChange(this.Name, this.ThreadCount);

                ThreadPoolState prevState;
                ChangeStateSafe(ThreadPoolState.Running, out prevState);
                if (prevState == ThreadPoolState.Stopped)
                    return;

                if (!_useOwnTaskScheduler)
                {
                    var threadData = new ThreadPrivateData(_threadPoolGlobals.GetOrCreateThisThreadData(startupData.CreateThreadLocalQueue));
                    startupData.ThreadProcedure(threadData, GetThreadRunningCancellationToken());
                }
                else
                {
                    Task threadTask = new Task(ThreadStartUpInsideTask, rawData, TaskCreationOptions.LongRunning);
                    Qoollo.Turbo.Threading.ServiceStuff.TaskHelper.SetTaskScheduler(threadTask, this.TaskScheduler);
                    Qoollo.Turbo.Threading.ServiceStuff.TaskHelper.ExecuteTaskEntry(threadTask);
                    if (threadTask.IsFaulted || threadTask.IsCanceled)
                        RethrowTaskException(threadTask);
                }
            }
            finally
            {
                _threadPoolGlobals.FreeThisThreadData();
                RemoveStoppedThread(Thread.CurrentThread);

                Profiling.Profiler.ThreadPoolThreadCountChange(this.Name, this.ThreadCount);
            }
        }

        /// <summary>
        /// Initialization of thread within Task (when <see cref="_useOwnTaskScheduler"/> setted to True)
        /// </summary>
        /// <param name="rawData">Start-up data (instance of <see cref="ThreadStartUpData"/>)</param>
        [System.Diagnostics.DebuggerNonUserCode]
        private void ThreadStartUpInsideTask(object rawData)
        {
            TurboContract.Requires(rawData != null, conditionString: "rawData != null");
            TurboContract.Assert(!_useOwnTaskScheduler || TaskScheduler.Current == this.TaskScheduler, conditionString: "!_useOwnTaskScheduler || TaskScheduler.Current == this.TaskScheduler");

            ThreadStartUpData startupData = (ThreadStartUpData)rawData;

            var threadData = new ThreadPrivateData(_threadPoolGlobals.GetOrCreateThisThreadData(startupData.CreateThreadLocalQueue));
            startupData.ThreadProcedure(threadData, GetThreadRunningCancellationToken());
        }


        /// <summary>
        /// Creates a new Thread (does not start that Thread)
        /// </summary>
        /// <param name="customName">Specific name for thread</param>
        /// <returns>Created thread</returns>
        private Thread CreateNewThread(string customName)
        {
            TurboContract.Ensures(TurboContract.Result<Thread>() != null);

            Thread res = new Thread(ThreadStartUpProc);
            int threadNum = Interlocked.Increment(ref _currentThreadNum);
            res.IsBackground = _isBackgroundThreads;
            if (string.IsNullOrEmpty(customName))
                res.Name = string.Format("{0} (#{1})", _name, threadNum);
            else
                res.Name = string.Format("{0} (#{1}). {2}", _name, threadNum, customName);

            return res;
        }


        /// <summary>
        /// Adds a new thread to the ThreadPool
        /// </summary>
        /// <param name="threadProc">Delegate to the main thread procedure</param>
        /// <param name="customName">Specific name of the thread</param>
        /// <param name="createThreadLocalQueue">Whether the local queue should be created</param>
        /// <returns>Created and started thread (null, when some error happened)</returns>
        protected Thread AddNewThread(Action<ThreadPrivateData, CancellationToken> threadProc, string customName = null, bool createThreadLocalQueue = true)
        {
            TurboContract.Requires(threadProc != null, conditionString: "threadProc != null");

            if (State == ThreadPoolState.Stopped)
                return null;

            Thread res = null;
            bool success = false;
            ThreadStartControllingToken startController = null;
            try
            {
                lock (_activeThread)
                {
                    if (State == ThreadPoolState.Stopped)
                        return null;

                    startController = new ThreadStartControllingToken();
                    res = this.CreateNewThread(customName);
                    res.Start(new ThreadStartUpData(threadProc, startController, createThreadLocalQueue));

                    try { }
                    finally
                    {
                        _activeThread.Add(res);
                        Interlocked.Increment(ref _activeThreadCount);
                        success = true;
                    }

                    TurboContract.Assert(_activeThread.Count == _activeThreadCount, conditionString: "_activeThread.Count == _activeThreadCount");
                }
            }
            catch (Exception ex)
            {
                throw new CantInitThreadException("Exception during thread creation in ThreadPool '" + Name + "'", ex);
            }
            finally
            {
                if (success)
                {
                    startController.SetOk();
                }
                else
                {
                    if (startController != null)
                        startController.SetFail();
                    res = null;
                }
            }

            return res;
        }


        /// <summary>
        /// Virtual method that allows to handle thread termination in child classes
        /// </summary>
        /// <param name="elem">Instance of the thread that is during the termination process</param>
        protected virtual void OnThreadRemove(Thread elem)
        {
            TurboContract.Requires(elem != null, conditionString: "elem != null");
        }

        /// <summary>
        /// Removes the thread from ThreadPool (removes it from <see cref="_activeThread"/> list and performs additional processes)
        /// </summary>
        /// <param name="elem">Thread to be removed</param>
        private void RemoveStoppedThread(Thread elem)
        {
            TurboContract.Requires(elem != null, conditionString: "elem != null");

            bool removeResult = false;
            lock (_activeThread)
            {
                try { }
                finally
                {
                    removeResult = _activeThread.Remove(elem);
                    if (removeResult)
                    {
                        Interlocked.Decrement(ref _activeThreadCount);

                        // Если поток последний и запрошена остановка, то переходим в состояние "остановлено"
                        if (_activeThread.Count == 0 && State == ThreadPoolState.StopRequested)
                        {
                            ThreadPoolState prevState;
                            bool stateChanged = ChangeStateSafe(ThreadPoolState.Stopped, out prevState);
                            TurboContract.Assert(stateChanged || prevState == ThreadPoolState.Stopped, "State was not changed to stopped");
                            if (stateChanged)
                                OnGoToStoppedState();
                        }
                    }
                    TurboContract.Assert(_activeThread.Count == _activeThreadCount, conditionString: "_activeThread.Count == _activeThreadCount");
                }
            }

            TurboContract.Assert(removeResult, "Remove Thread in Pool failed");
            if (removeResult)
                OnThreadRemove(elem);
        }

        /// <summary>
        /// Virtual method that allows to handle ThreadPool termination in child classes
        /// </summary>
        /// <param name="waitForStop">Whether the current thread should be blocked until all processing threads are be completed</param>
        /// <param name="letFinishProcess">Whether all items that have already been added must be processed before stopping</param>
        /// <param name="completeAdding">Marks that new items cannot be added to work items queue</param>
        protected virtual void OnThreadPoolStop(bool waitForStop, bool letFinishProcess, bool completeAdding)
        {
        }

        /// <summary>
        /// Clears the global queue after stopping
        /// </summary>
        private void CleanUpMainQueueAfterStop()
        {
            TurboContract.Assert(State == ThreadPoolState.Stopped, conditionString: "State == ThreadPoolState.Stopped");

            ThreadPoolWorkItem item = null;
            while (_threadPoolGlobals.TryTakeItemSafeFromGlobalQueue(out item))
                this.CancelWorkItem(item);
        }

        /// <summary>
        /// Marks that new items cannot be added to work items queue
        /// </summary>
        public void CompleteAdding()
        {
            _completeAdding = true;
            if (State == ThreadPoolState.Stopped)
                CleanUpMainQueueAfterStop();
        }

        /// <summary>
        /// Stops ThreadPool and changes state to <see cref="ThreadPoolState.StopRequested"/> (core logic)
        /// </summary>
        /// <param name="waitForStop">Whether the current thread should be blocked until all processing threads are be completed</param>
        /// <param name="letFinishProcess">Whether all items that have already been added must be processed before stopping</param>
        /// <param name="completeAdding">Marks that new items cannot be added to work items queue</param>
        protected void StopThreadPool(bool waitForStop, bool letFinishProcess, bool completeAdding)
        {
            if (this.IsStopRequestedOrStopped)
            {
                if (State == ThreadPoolState.Stopped && (completeAdding || this.IsAddingCompleted))
                {
                    if (completeAdding)
                        _completeAdding = completeAdding;
                    CleanUpMainQueueAfterStop();
                }
                if (waitForStop)
                {
                    this.WaitUntilStop();
                }
                return;
            }

            Thread[] waitFor = null;
            bool stopRequestedBefore = false;

            lock (_activeThread)
            {
                stopRequestedBefore = this.IsStopRequestedOrStopped;
                if (!stopRequestedBefore)
                {
                    _letFinishProcess = letFinishProcess;

                    if (waitForStop)
                        waitFor = _activeThread.ToArray();

                    ThreadPoolState prevState;
                    bool stateChangedToStopRequested = ChangeStateSafe(ThreadPoolState.StopRequested, out prevState);
                    TurboContract.Assert(stateChangedToStopRequested || prevState == ThreadPoolState.Stopped, conditionString: "stateChangedToStopRequested || prevState == ThreadPoolState.Stopped");
                    if (_activeThread.Count == 0 && prevState != ThreadPoolState.Stopped)
                    {
                        // Если потоков нет, то переходим в состояние "остановлено"
                        bool stateChanged = ChangeStateSafe(ThreadPoolState.Stopped, out prevState);
                        TurboContract.Assert(stateChanged || prevState == ThreadPoolState.Stopped, "State was not changed to stopped");
                        if (stateChanged)
                            OnGoToStoppedState();
                    }

                    _threadRunningCancellation.Cancel();
                    if (completeAdding)
                        _completeAdding = completeAdding;

                    OnThreadPoolStop(waitForStop, letFinishProcess, completeAdding);
                }
            }


            if (waitForStop && waitFor != null)
            {
                while (waitFor.Length > 0)
                {
                    for (int i = 0; i < waitFor.Length; i++)
                        waitFor[i].Join();

                    lock (_activeThread)
                    {
                        waitFor = _activeThread.ToArray();
                    }
                }
            }
            if (stopRequestedBefore)
            {
                if (State == ThreadPoolState.Stopped && (completeAdding || this.IsAddingCompleted))
                {
                    if (completeAdding)
                        _completeAdding = completeAdding;
                    CleanUpMainQueueAfterStop();
                }
                if (waitForStop)
                {
                    this.WaitUntilStop();
                }
            }
        }


        /// <summary>
        /// Processes new ThreadPoolWorkItem when it was just received from user and is still not in the queue
        /// </summary>
        /// <param name="item">Work item</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void PrepareWorkItem(ThreadPoolWorkItem item)
        {
            TurboContract.Requires(item != null, conditionString: "item != null");
            if (_restoreExecutionContext && !ExecutionContext.IsFlowSuppressed())
                item.CaptureExecutionContext(!_useOwnSyncContext);
        }
        /// <summary>
        /// Executes the ThreadPoolWorkItem (should always be used to execute work items)
        /// </summary>
        /// <param name="item">Work item</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void RunWorkItem(ThreadPoolWorkItem item)
        {
            TurboContract.Requires(item != null, conditionString: "item != null");
            TurboContract.Assert(!_useOwnSyncContext || (SynchronizationContext.Current == _synchroContext), conditionString: "!_useOwnSyncContext || (SynchronizationContext.Current == _synchroContext)");

            Profiling.ProfilingTimer timer = new Profiling.ProfilingTimer();
            timer.StartTime();

            item.Run(_restoreExecutionContext, _useOwnSyncContext);

            Profiling.Profiler.ThreadPoolWorkProcessed(this.Name, timer.StopTime());
        }
        /// <summary>
        /// Transfers ThreadPoolWorkItem to cancelled state
        /// </summary>
        /// <param name="item">Work item</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void CancelWorkItem(ThreadPoolWorkItem item)
        {
            TurboContract.Requires(item != null, conditionString: "item != null");
            item.Cancel();
            Profiling.Profiler.ThreadPoolWorkCancelled(this.Name);
        }


        /// <summary>
        /// Extends the capacity of GlobalQueue by specified value
        /// </summary>
        /// <param name="extensionVal">Value by which the increase is made</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExtendGlobalQueueCapacity(int extensionVal)
        {
            TurboContract.Requires(extensionVal >= 0, conditionString: "extensionVal >= 0");
            TurboContract.Assert(State != ThreadPoolState.Stopped, conditionString: "State != ThreadPoolState.Stopped");

            _threadPoolGlobals.ExtendGlobalQueueCapacity(extensionVal);
        }

        /// <summary>
        /// Enqueues ThreadPoolWorkItem. 
        /// First it attempts to enqueue work item to thread local queue. If it is not possible, it enqueues to the global queue.
        /// When <see cref="ThreadPoolWorkItem.PreferFairness"/> setted to True, it alway enqueues to global queue.
        /// </summary>
        /// <param name="item">Work item</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void AddWorkItemToQueue(ThreadPoolWorkItem item)
        {
            TurboContract.Requires(item != null, conditionString: "item != null");
            TurboContract.Assert(!_restoreExecutionContext || !item.AllowExecutionContextFlow || item.CapturedContext != null, conditionString: "!_restoreExecutionContext || !item.AllowExecutionContextFlow || item.CapturedContext != null");
            TurboContract.Assert(State != ThreadPoolState.Stopped, conditionString: "State != ThreadPoolState.Stopped");

            item.StartStoreTimer();
            _threadPoolGlobals.AddItem(item, item.PreferFairness);
            Profiling.Profiler.ThreadPoolWorkItemsCountIncreased(this.Name, this.GlobalQueueWorkItemCount, this.QueueCapacity);
        }
        /// <summary>
        /// Attempts to enqueues ThreadPoolWorkItem. 
        /// First it attempts to enqueue work item to thread local queue. If it is not possible, it attempts to enqueue to the global queue.
        /// When <see cref="ThreadPoolWorkItem.PreferFairness"/> setted to True, it alway attempts to enqueue to global queue.
        /// </summary>
        /// <param name="item">Work item</param>
        /// <returns>Whether the work item was enqueued successfully</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool TryAddWorkItemToQueue(ThreadPoolWorkItem item)
        {
            TurboContract.Requires(item != null, conditionString: "item != null");
            TurboContract.Assert(!_restoreExecutionContext || !item.AllowExecutionContextFlow || item.CapturedContext != null, conditionString: "!_restoreExecutionContext || !item.AllowExecutionContextFlow || item.CapturedContext != null");
            TurboContract.Assert(State != ThreadPoolState.Stopped, conditionString: "State != ThreadPoolState.Stopped");

            item.StartStoreTimer();
            var result = _threadPoolGlobals.TryAddItem(item, item.PreferFairness);
            if (result)
                Profiling.Profiler.ThreadPoolWorkItemsCountIncreased(this.Name, this.GlobalQueueWorkItemCount, this.QueueCapacity);
            else
                Profiling.Profiler.ThreadPoolWorkItemRejectedInTryAdd(this.Name);
            return result;
        }
        /// <summary>
        /// Takes work item from ThreadPool queue.
        /// First it attempts to take item from local thread queue. If it is not possible, it deques work item from the global queue.
        /// Additionally it can steal work item from local queues of other ThreadPool threads.
        /// </summary>
        /// <param name="privateData">Thread specific data</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Taken work item</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected ThreadPoolWorkItem TakeWorkItemFromQueue(ThreadPrivateData privateData, CancellationToken token)
        {
            TurboContract.Requires(privateData != null, conditionString: "privateData != null");
            TurboContract.Ensures(TurboContract.Result<ThreadPoolWorkItem>() != null);
            TurboContract.Assert(State != ThreadPoolState.Stopped, conditionString: "State != ThreadPoolState.Stopped");

            var result = _threadPoolGlobals.TakeItem(privateData.LocalData, token);
            Profiling.Profiler.ThreadPoolWaitingInQueueTime(this.Name, result.StopStoreTimer());
            Profiling.Profiler.ThreadPoolWorkItemsCountDecreased(this.Name, this.GlobalQueueWorkItemCount, this.QueueCapacity);
            return result;
        }
        /// <summary>
        /// Attempts to take work item from ThreadPool queue.
        /// First it attempts to take item from local thread queue. If it is not possible, it attempts to take work item from the global queue.
        /// Additionally it can steal work item from local queues of other ThreadPool threads.
        /// </summary>
        /// <param name="privateData">Thread specific data</param>
        /// <param name="item">Taken work item</param>
        /// <returns>Whether the work item was taken successfully</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool TryTakeWorkItemFromQueue(ThreadPrivateData privateData, out ThreadPoolWorkItem item)
        {
            TurboContract.Requires(privateData != null, conditionString: "privateData != null");
            TurboContract.Ensures(TurboContract.Result<bool>() == false || TurboContract.ValueAtReturn(out item) != null);
            TurboContract.Assert(State != ThreadPoolState.Stopped, conditionString: "State != ThreadPoolState.Stopped");

            var result = _threadPoolGlobals.TryTakeItem(privateData.LocalData, out item);
            if (result)
            {
                Profiling.Profiler.ThreadPoolWaitingInQueueTime(this.Name, item.StopStoreTimer());
                Profiling.Profiler.ThreadPoolWorkItemsCountDecreased(this.Name, this.GlobalQueueWorkItemCount, this.QueueCapacity);
            }
            return result;
        }
        /// <summary>
        /// Attempts to take work item from ThreadPool queue.
        /// First it attempts to take item from local thread queue. If it is not possible, it attempts to take work item from the global queue.
        /// Additionally it can steal work item from local queues of other ThreadPool threads.
        /// </summary>
        /// <param name="privateData">Thread specific data</param>
        /// <param name="item">Taken work item (when successful)</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <param name="token">Cancellation token</param>
        /// <param name="throwOnCancellation">Whether the <see cref="OperationCanceledException"/> should be thrown on Cancellation</param>
        /// <returns>Whether the work item was taken successfully</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool TryTakeWorkItemFromQueue(ThreadPrivateData privateData, out ThreadPoolWorkItem item, int timeout, CancellationToken token, bool throwOnCancellation)
        {
            TurboContract.Requires(privateData != null, conditionString: "privateData != null");
            TurboContract.Ensures(TurboContract.Result<bool>() == false || TurboContract.ValueAtReturn(out item) != null);
            TurboContract.Assert(State != ThreadPoolState.Stopped, conditionString: "State != ThreadPoolState.Stopped");

            var result = _threadPoolGlobals.TryTakeItem(privateData.LocalData, true, true, out item, timeout, token, throwOnCancellation);
            if (result)
            {
                Profiling.Profiler.ThreadPoolWaitingInQueueTime(this.Name, item.StopStoreTimer());
                Profiling.Profiler.ThreadPoolWorkItemsCountDecreased(this.Name, this.GlobalQueueWorkItemCount, this.QueueCapacity);
            }
            return result;
        }
        /// <summary>
        /// Attempts to take work item from ThreadPool queue.
        /// First it attempts to take item from local thread queue. If it is not possible, it attempts to take work item from the global queue.
        /// Stealing from local queues of other ThreadPool threads is not performed.
        /// </summary>
        /// <param name="privateData">Thread specific data</param>
        /// <param name="item">Taken work item (when successful)</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <param name="token">Cancellation token</param>
        /// <param name="throwOnCancellation">Whether the <see cref="OperationCanceledException"/> should be thrown on Cancellation</param>
        /// <returns>Whether the work item was taken successfully</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool TryTakeWorkItemFromQueueWithoutSteal(ThreadPrivateData privateData, out ThreadPoolWorkItem item, int timeout, CancellationToken token, bool throwOnCancellation)
        {
            TurboContract.Requires(privateData != null, conditionString: "privateData != null");
            TurboContract.Ensures(TurboContract.Result<bool>() == false || TurboContract.ValueAtReturn(out item) != null);
            TurboContract.Assert(State != ThreadPoolState.Stopped, conditionString: "State != ThreadPoolState.Stopped");

            var result = _threadPoolGlobals.TryTakeItem(privateData.LocalData, true, false, out item, timeout, token, throwOnCancellation);
            if (result)
            {
                Profiling.Profiler.ThreadPoolWaitingInQueueTime(this.Name, item.StopStoreTimer());
                Profiling.Profiler.ThreadPoolWorkItemsCountDecreased(this.Name, this.GlobalQueueWorkItemCount, this.QueueCapacity);
            }
            return result;
        }
        /// <summary>
        /// Attempts to take work item from ThreadPool queue.
        /// It does not attempt to take item from local queue. 
        /// It attempts to take item from global queue or steals it from local queues of other ThreadPool threads.
        /// </summary>
        /// <param name="privateData">Thread specific data</param>
        /// <param name="item">Taken work item (when successful)</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <param name="token">Cancellation token</param>
        /// <param name="throwOnCancellation">Whether the <see cref="OperationCanceledException"/> should be thrown on Cancellation</param>
        /// <returns>Whether the work item was taken successfully</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool TryTakeWorkItemFromQueueWithoutLocalSearch(ThreadPrivateData privateData, out ThreadPoolWorkItem item, int timeout, CancellationToken token, bool throwOnCancellation)
        {
            TurboContract.Requires(privateData != null, conditionString: "privateData != null");
            TurboContract.Ensures(TurboContract.Result<bool>() == false || TurboContract.ValueAtReturn(out item) != null);
            TurboContract.Assert(State != ThreadPoolState.Stopped, conditionString: "State != ThreadPoolState.Stopped");

            var result = _threadPoolGlobals.TryTakeItem(privateData.LocalData, false, true, out item, timeout, token, throwOnCancellation);
            if (result)
            {
                Profiling.Profiler.ThreadPoolWaitingInQueueTime(this.Name, item.StopStoreTimer());
                Profiling.Profiler.ThreadPoolWorkItemsCountDecreased(this.Name, this.GlobalQueueWorkItemCount, this.QueueCapacity);
            }
            return result;
        }



        /// <summary>
        /// Runs an asynchronous action in another synchronization context
        /// </summary>
        /// <param name="act">Delegate to be executed</param>
        /// <param name="state">The object passed to the delegate</param>
        void ICustomSynchronizationContextSupplier.RunAsync(System.Threading.SendOrPostCallback act, object state)
        {
            TurboContract.Requires(act != null, conditionString: "act != null");
            var item = new SendOrPostCallbackThreadPoolWorkItem(act, state, true, false);
            this.AddWorkItem(item);
        }
        /// <summary>
        /// Runs a synchronous action in another synchronization context
        /// </summary>
        /// <param name="act">Delegate to be executed</param>
        /// <param name="state">The object passed to the delegate</param>
        void ICustomSynchronizationContextSupplier.RunSync(System.Threading.SendOrPostCallback act, object state)
        {
            TurboContract.Requires(act != null, conditionString: "act != null");
            var item = new SendOrPostCallbackSyncThreadPoolWorkItem(act, state, true, true);
            this.AddWorkItem(item);
            item.Wait();
        }

        /// <summary>
        /// Runs action in another context
        /// </summary>
        /// <param name="act">Delegate for action to be executed</param>
        /// <param name="flowContext">Whether the ExecutionContext should be flowed</param>
        void IContextSwitchSupplier.Run(Action act, bool flowContext)
        {
            TurboContract.Requires(act != null, conditionString: "act != null");
            var item = new ActionThreadPoolWorkItem(act, flowContext, false);
            this.AddWorkItem(item);
        }
        /// <summary>
        /// Runs action in another context
        /// </summary>
        /// <param name="act">Delegate for action to be executed</param>
        /// <param name="state">State object that will be passed to '<paramref name="act"/>' as argument</param>
        /// <param name="flowContext">hether the ExecutionContext should be flowed</param>
        void IContextSwitchSupplier.RunWithState(Action<object> act, object state, bool flowContext)
        {
            TurboContract.Requires(act != null, conditionString: "act != null");
            var item = new ActionThreadPoolWorkItem<object>(act, state, flowContext, false);
            this.AddWorkItem(item);
        }


        /// <summary>
        /// Stops ThreadPool and changes state to <see cref="ThreadPoolState.StopRequested"/>
        /// </summary>
        /// <param name="waitForStop">Whether the current thread should be blocked until all processing threads are be completed</param>
        /// <param name="letFinishProcess">Whether all items that have already been added must be processed before stopping</param>
        /// <param name="completeAdding">Marks that new items cannot be added to work items queue</param>
        public virtual void Dispose(bool waitForStop, bool letFinishProcess, bool completeAdding)
        {
            StopThreadPool(waitForStop, letFinishProcess, completeAdding);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Cleans-up resources
        /// </summary>
        /// <param name="isUserCall">Is it called explicitly by user (False - from finalizer)</param>
        protected override void Dispose(bool isUserCall)
        {
            if (isUserCall && State == ThreadPoolState.Stopped)
                CleanUpMainQueueAfterStop();

            if (State != ThreadPoolState.StopRequested && State != ThreadPoolState.Stopped)
            {
                TurboContract.Assert(isUserCall, "ThreadPool finalizer called. You should dispose ThreadPool by hand. ThreadPoolName: " + this.Name);

                if (isUserCall)
                    StopThreadPool(true, false, true);

                ThreadPoolState prevState;
                ChangeStateSafe(ThreadPoolState.StopRequested, out prevState);
                ChangeStateSafe(ThreadPoolState.Stopped, out prevState);

                if (_threadRunningCancellation != null)
                    _threadRunningCancellation.Cancel();

                if (!isUserCall)
                    Profiling.Profiler.ThreadPoolDisposed(Name, true);
            }
            base.Dispose(isUserCall);
        }
    }
}
