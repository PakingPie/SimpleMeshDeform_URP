// SDFLODManager.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Manages Level of Detail for SDF operations and mesh generation.
/// Automatically adjusts resolution based on camera distance and performance.
/// </summary>
public class SDFLODManager : MonoBehaviour
{
    [System.Serializable]
    public class LODLevel
    {
        public float Distance;
        public Vector3Int Resolution;
        public int MaxVertices;
        public float UpdateInterval;
    }

    [Header("LOD Settings")]
    [SerializeField] private List<LODLevel> _lodLevels = new List<LODLevel>
    {
        new LODLevel { Distance = 5f, Resolution = new Vector3Int(128, 128, 128), MaxVertices = 500000, UpdateInterval = 0f },
        new LODLevel { Distance = 15f, Resolution = new Vector3Int(64, 64, 64), MaxVertices = 200000, UpdateInterval = 0.1f },
        new LODLevel { Distance = 30f, Resolution = new Vector3Int(32, 32, 32), MaxVertices = 50000, UpdateInterval = 0.25f },
        new LODLevel { Distance = 60f, Resolution = new Vector3Int(16, 16, 16), MaxVertices = 10000, UpdateInterval = 0.5f }
    };

    [Header("Performance")]
    [SerializeField] private float _targetFrameTime = 16.67f; // 60 FPS
    [SerializeField] private bool _adaptiveQuality = true;
    [SerializeField] private float _qualityAdjustSpeed = 0.5f;

    [Header("References")]
    [SerializeField] private MeshEditController _editController;
    [SerializeField] private Camera _mainCamera;

    private int _currentLOD = 0;
    private float _lastUpdateTime;
    private float _qualityMultiplier = 1f;
    private Queue<float> _frameTimeHistory = new Queue<float>();
    private const int FrameHistorySize = 30;

    public int CurrentLOD => _currentLOD;
    public Vector3Int CurrentResolution => GetCurrentResolution();
    public float QualityMultiplier => _qualityMultiplier;

    public event System.Action<int> OnLODChanged;

    private void Start()
    {
        if (_mainCamera == null)
        {
            _mainCamera = Camera.main;
        }

        if (_editController == null)
        {
            _editController = GetComponent<MeshEditController>();
        }

        SortLODLevels();
    }

    private void Update()
    {
        UpdateFrameTimeHistory();
        
        if (_adaptiveQuality)
        {
            AdjustQuality();
        }

        int newLOD = CalculateLOD();
        
        if (newLOD != _currentLOD)
        {
            SetLOD(newLOD);
        }
    }

    private void SortLODLevels()
    {
        _lodLevels.Sort((a, b) => a.Distance.CompareTo(b.Distance));
    }

    private int CalculateLOD()
    {
        if (_mainCamera == null) return 0;

        float distance = Vector3.Distance(_mainCamera.transform.position, transform.position);
        
        // Apply quality multiplier
        distance /= _qualityMultiplier;

        for (int i = 0; i < _lodLevels.Count; i++)
        {
            if (distance < _lodLevels[i].Distance)
            {
                return i;
            }
        }

        return _lodLevels.Count - 1;
    }

    private void SetLOD(int lodIndex)
    {
        lodIndex = Mathf.Clamp(lodIndex, 0, _lodLevels.Count - 1);
        
        if (lodIndex == _currentLOD) return;

        _currentLOD = lodIndex;
        LODLevel level = _lodLevels[lodIndex];

        // Apply LOD settings to edit controller
        // This would require exposing methods on MeshEditController to change resolution dynamically
        Debug.Log($"LOD changed to level {lodIndex}: Resolution {level.Resolution}");

        OnLODChanged?.Invoke(lodIndex);
    }

    private void UpdateFrameTimeHistory()
    {
        float frameTime = Time.deltaTime * 1000f;
        
        _frameTimeHistory.Enqueue(frameTime);
        
        while (_frameTimeHistory.Count > FrameHistorySize)
        {
            _frameTimeHistory.Dequeue();
        }
    }

    private void AdjustQuality()
    {
        if (_frameTimeHistory.Count < FrameHistorySize) return;

        float averageFrameTime = 0f;
        foreach (float time in _frameTimeHistory)
        {
            averageFrameTime += time;
        }
        averageFrameTime /= _frameTimeHistory.Count;

        // Adjust quality based on frame time
        if (averageFrameTime > _targetFrameTime * 1.2f)
        {
            // Decrease quality
            _qualityMultiplier -= _qualityAdjustSpeed * Time.deltaTime;
        }
        else if (averageFrameTime < _targetFrameTime * 0.8f)
        {
            // Increase quality
            _qualityMultiplier += _qualityAdjustSpeed * Time.deltaTime;
        }

        _qualityMultiplier = Mathf.Clamp(_qualityMultiplier, 0.25f, 2f);
    }

    public Vector3Int GetCurrentResolution()
    {
        if (_currentLOD >= _lodLevels.Count) return new Vector3Int(64, 64, 64);
        
        Vector3Int baseRes = _lodLevels[_currentLOD].Resolution;
        
        // Apply quality multiplier
        return new Vector3Int(
            Mathf.RoundToInt(baseRes.x * _qualityMultiplier),
            Mathf.RoundToInt(baseRes.y * _qualityMultiplier),
            Mathf.RoundToInt(baseRes.z * _qualityMultiplier)
        );
    }

    public float GetUpdateInterval()
    {
        if (_currentLOD >= _lodLevels.Count) return 0f;
        return _lodLevels[_currentLOD].UpdateInterval;
    }

    public int GetMaxVertices()
    {
        if (_currentLOD >= _lodLevels.Count) return 500000;
        return _lodLevels[_currentLOD].MaxVertices;
    }

    /// <summary>
    /// Force a specific LOD level (disables automatic LOD).
    /// </summary>
    public void ForceLOD(int lodIndex)
    {
        _adaptiveQuality = false;
        SetLOD(lodIndex);
    }

    /// <summary>
    /// Resume automatic LOD selection.
    /// </summary>
    public void ResumeAutomaticLOD()
    {
        _adaptiveQuality = true;
    }
}