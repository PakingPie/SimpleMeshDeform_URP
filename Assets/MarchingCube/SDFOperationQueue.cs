// SDFOperationQueue.cs
using UnityEngine;
using System;
using System.Collections.Generic;

/// <summary>
/// Queues and batches SDF operations for better performance.
/// Prevents frame drops during heavy editing.
/// </summary>
public class SDFOperationQueue : MonoBehaviour
{
    public enum OperationType
    {
        CSG,
        Deformation,
        MeshRegeneration
    }

    public struct QueuedOperation
    {
        public OperationType Type;
        public Action Operation;
        public float Priority;
        public float EnqueueTime;
    }

    [Header("Settings")]
    [SerializeField] private float _maxTimePerFrame = 4f; // milliseconds
    [SerializeField] private int _maxOperationsPerFrame = 10;
    [SerializeField] private bool _batchSimilarOperations = true;
    [SerializeField] private float _batchWindow = 0.05f; // seconds

    private Queue<QueuedOperation> _operationQueue = new Queue<QueuedOperation>();
    private List<QueuedOperation> _pendingBatch = new List<QueuedOperation>();
    private float _lastBatchTime;
    private bool _isBatching;

    private System.Diagnostics.Stopwatch _frameStopwatch = new System.Diagnostics.Stopwatch();

    public int QueuedOperationCount => _operationQueue.Count + _pendingBatch.Count;
    public bool IsProcessing => _operationQueue.Count > 0;

    public event Action OnQueueEmpty;
    public event Action<int> OnQueueSizeChanged;

    private void Update()
    {
        ProcessBatch();
        ProcessQueue();
    }

    /// <summary>
    /// Enqueue an operation for processing.
    /// </summary>
    public void Enqueue(Action operation, OperationType type = OperationType.CSG, float priority = 0f)
    {
        QueuedOperation queuedOp = new QueuedOperation
        {
            Type = type,
            Operation = operation,
            Priority = priority,
            EnqueueTime = Time.time
        };

        if (_batchSimilarOperations && type == OperationType.Deformation)
        {
            _pendingBatch.Add(queuedOp);
            _isBatching = true;
            _lastBatchTime = Time.time;
        }
        else
        {
            _operationQueue.Enqueue(queuedOp);
        }

        OnQueueSizeChanged?.Invoke(QueuedOperationCount);
    }

    /// <summary>
    /// Enqueue a high-priority operation that will be processed first.
    /// </summary>
    public void EnqueueImmediate(Action operation, OperationType type = OperationType.CSG)
    {
        // Convert queue to list, insert at front, convert back
        List<QueuedOperation> list = new List<QueuedOperation>(_operationQueue);
        
        list.Insert(0, new QueuedOperation
        {
            Type = type,
            Operation = operation,
            Priority = float.MaxValue,
            EnqueueTime = Time.time
        });

        _operationQueue = new Queue<QueuedOperation>(list);
        OnQueueSizeChanged?.Invoke(QueuedOperationCount);
    }

    /// <summary>
    /// Clear all pending operations.
    /// </summary>
    public void Clear()
    {
        _operationQueue.Clear();
        _pendingBatch.Clear();
        _isBatching = false;
        OnQueueSizeChanged?.Invoke(0);
    }

    /// <summary>
    /// Force process all remaining operations immediately.
    /// May cause frame drops.
    /// </summary>
    public void Flush()
    {
        FlushBatch();
        
        while (_operationQueue.Count > 0)
        {
            var op = _operationQueue.Dequeue();
            try
            {
                op.Operation?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogError($"Operation failed: {e.Message}");
            }
        }

        OnQueueEmpty?.Invoke();
        OnQueueSizeChanged?.Invoke(0);
    }

    private void ProcessBatch()
    {
        if (!_isBatching || _pendingBatch.Count == 0) return;

        // Check if batch window has elapsed
        if (Time.time - _lastBatchTime >= _batchWindow)
        {
            FlushBatch();
        }
    }

    private void FlushBatch()
    {
        if (_pendingBatch.Count == 0) return;

        // Combine similar operations if possible
        if (_pendingBatch.Count > 1)
        {
            // For deformations, we could combine multiple brush strokes
            // For now, just queue them all
            foreach (var op in _pendingBatch)
            {
                _operationQueue.Enqueue(op);
            }
        }
        else
        {
            _operationQueue.Enqueue(_pendingBatch[0]);
        }

        _pendingBatch.Clear();
        _isBatching = false;
    }

    private void ProcessQueue()
    {
        if (_operationQueue.Count == 0) return;

        _frameStopwatch.Restart();
        int operationsProcessed = 0;

        while (_operationQueue.Count > 0 && 
               operationsProcessed < _maxOperationsPerFrame &&
               _frameStopwatch.ElapsedMilliseconds < _maxTimePerFrame)
        {
            var op = _operationQueue.Dequeue();
            
            try
            {
                op.Operation?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogError($"Operation failed: {e.Message}");
            }

            operationsProcessed++;
        }

        _frameStopwatch.Stop();

        if (_operationQueue.Count == 0)
        {
            OnQueueEmpty?.Invoke();
        }

        OnQueueSizeChanged?.Invoke(QueuedOperationCount);
    }
}