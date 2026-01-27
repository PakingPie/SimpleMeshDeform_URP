// SDFVolumeCache.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Caches SDF volumes for undo/redo and quick switching between states.
/// </summary>
public class SDFVolumeCache : System.IDisposable
{
    public struct CacheEntry
    {
        public RenderTexture Volume;
        public string Name;
        public float Timestamp;
    }

    private List<CacheEntry> _cache = new List<CacheEntry>();
    private int _maxCacheSize;
    private Vector3Int _resolution;
    private bool _disposed;

    public int CacheSize => _cache.Count;
    public int MaxCacheSize => _maxCacheSize;

    public SDFVolumeCache(int maxSize, Vector3Int resolution)
    {
        _maxCacheSize = maxSize;
        _resolution = resolution;
    }

    /// <summary>
    /// Save current SDF volume state to cache.
    /// </summary>
    public void Push(RenderTexture source, string name = null)
    {
        // Create copy
        RenderTexture copy = CreateVolumeCopy(source);
        
        CacheEntry entry = new CacheEntry
        {
            Volume = copy,
            Name = name ?? $"State_{_cache.Count}",
            Timestamp = Time.time
        };

        _cache.Add(entry);

        // Remove oldest if over limit
        while (_cache.Count > _maxCacheSize)
        {
            if (_cache[0].Volume != null)
            {
                _cache[0].Volume.Release();
            }
            _cache.RemoveAt(0);
        }
    }

    /// <summary>
    /// Restore most recent cached state.
    /// </summary>
    public bool Pop(RenderTexture destination)
    {
        if (_cache.Count == 0) return false;

        CacheEntry entry = _cache[_cache.Count - 1];
        _cache.RemoveAt(_cache.Count - 1);

        if (entry.Volume != null)
        {
            Graphics.CopyTexture(entry.Volume, destination);
            entry.Volume.Release();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Peek at a cached state without removing it.
    /// </summary>
    public bool Peek(int index, RenderTexture destination)
    {
        if (index < 0 || index >= _cache.Count) return false;

        CacheEntry entry = _cache[index];
        
        if (entry.Volume != null)
        {
            Graphics.CopyTexture(entry.Volume, destination);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Get info about cached state.
    /// </summary>
    public CacheEntry? GetInfo(int index)
    {
        if (index < 0 || index >= _cache.Count) return null;
        return _cache[index];
    }

    /// <summary>
    /// Clear all cached states.
    /// </summary>
    public void Clear()
    {
        foreach (var entry in _cache)
        {
            if (entry.Volume != null)
            {
                entry.Volume.Release();
            }
        }
        _cache.Clear();
    }

    private RenderTexture CreateVolumeCopy(RenderTexture source)
    {
        RenderTexture copy = new RenderTexture(source.width, source.height, 0, source.format)
        {
            dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
            volumeDepth = source.volumeDepth,
            enableRandomWrite = true,
            filterMode = source.filterMode,
            wrapMode = source.wrapMode
        };
        copy.Create();
        
        Graphics.CopyTexture(source, copy);
        
        return copy;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
    }
}

/// <summary>
/// Manages undo/redo for mesh editing operations.
/// </summary>
public class SDFUndoSystem : MonoBehaviour
{
    [SerializeField] private MeshEditController _editController;
    [SerializeField] private int _maxUndoSteps = 20;
    [SerializeField] private KeyCode _undoKey = KeyCode.Z;
    [SerializeField] private KeyCode _redoKey = KeyCode.Y;

    private SDFVolumeCache _undoStack;
    private SDFVolumeCache _redoStack;
    private bool _initialized;

    public int UndoCount => _undoStack?.CacheSize ?? 0;
    public int RedoCount => _redoStack?.CacheSize ?? 0;
    public bool CanUndo => UndoCount > 0;
    public bool CanRedo => RedoCount > 0;

    public event System.Action OnUndoPerformed;
    public event System.Action OnRedoPerformed;
    public event System.Action OnStateChanged;

    private void Start()
    {
        if (_editController == null)
        {
            _editController = GetComponent<MeshEditController>();
        }

        if (_editController != null)
        {
            // Use lambda to match Action delegate signature
            _editController.OnOperationComplete += OnOperationCompleteHandler;
        }
    }

    private void OnDestroy()
    {
        if (_editController != null)
        {
            _editController.OnOperationComplete -= OnOperationCompleteHandler;
        }
        
        _undoStack?.Dispose();
        _redoStack?.Dispose();
    }

    private void Update()
    {
        // Handle keyboard shortcuts
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        
        if (ctrl && Input.GetKeyDown(_undoKey))
        {
            Undo();
        }
        else if (ctrl && Input.GetKeyDown(_redoKey))
        {
            Redo();
        }
    }

    /// <summary>
    /// Handler for OnOperationComplete event (matches Action delegate).
    /// </summary>
    private void OnOperationCompleteHandler()
    {
        SaveUndoState();
    }

    private void Initialize()
    {
        if (_initialized || _editController?.SDFVolume == null) return;

        Vector3Int resolution = _editController.SDFVolume.Resolution;
        _undoStack = new SDFVolumeCache(_maxUndoSteps, resolution);
        _redoStack = new SDFVolumeCache(_maxUndoSteps, resolution);
        _initialized = true;
    }

    /// <summary>
    /// Save current state for undo.
    /// </summary>
    public void SaveUndoState(string name = null)
    {
        Initialize();
        
        if (_editController?.SDFVolume?.VolumeTexture == null) return;

        _undoStack.Push(_editController.SDFVolume.VolumeTexture, name);
        _redoStack.Clear(); // Clear redo stack on new action
        
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Undo last operation.
    /// </summary>
    public void Undo()
    {
        if (!CanUndo) return;

        // Save current state to redo stack
        if (_editController?.SDFVolume?.VolumeTexture != null)
        {
            _redoStack.Push(_editController.SDFVolume.VolumeTexture, "Redo");
        }

        // Restore previous state
        if (_undoStack.Pop(_editController.SDFVolume.VolumeTexture))
        {
            _editController.RegenerateMesh();
            OnUndoPerformed?.Invoke();
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Redo last undone operation.
    /// </summary>
    public void Redo()
    {
        if (!CanRedo) return;

        // Save current state to undo stack
        if (_editController?.SDFVolume?.VolumeTexture != null)
        {
            _undoStack.Push(_editController.SDFVolume.VolumeTexture, "Undo");
        }

        // Restore redo state
        if (_redoStack.Pop(_editController.SDFVolume.VolumeTexture))
        {
            _editController.RegenerateMesh();
            OnRedoPerformed?.Invoke();
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Clear all undo/redo history.
    /// </summary>
    public void ClearHistory()
    {
        _undoStack?.Clear();
        _redoStack?.Clear();
        OnStateChanged?.Invoke();
    }
}