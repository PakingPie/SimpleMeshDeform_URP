// MeshEditorSetup.cs
using UnityEngine;

/// <summary>
/// Example setup script showing how to integrate all components.
/// Attach this to a GameObject with a MeshFilter to make it editable.
/// </summary>
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class MeshEditorSetup : MonoBehaviour
{
    [Header("Compute Shaders")]
    [SerializeField] private ComputeShader _sdfGenerator;
    [SerializeField] private ComputeShader _sdfOperations;
    [SerializeField] private ComputeShader _marchingCubes;
    [SerializeField] private Material _previewMaterial;

    [Header("Settings")]
    [SerializeField] private BrushSettings _brushSettings;
    [SerializeField] private bool _enableCuttingTool = true;
    [SerializeField] private bool _enableDeformBrush = true;
    [SerializeField] private bool _enableUndoSystem = true;

    [Header("Optional Components")]
    [SerializeField] private bool _enableLOD = false;
    [SerializeField] private bool _enableOperationQueue = true;

    private MeshEditController _editController;
    private CuttingTool _cuttingTool;
    private DeformBrush _deformBrush;
    private SDFUndoSystem _undoSystem;
    private SDFLODManager _lodManager;
    private SDFOperationQueue _operationQueue;

    private void Awake()
    {
        SetupComponents();
    }

    private void Start()
    {
        InitializeEditor();
    }

    private void SetupComponents()
    {
        // Add core controller
        _editController = gameObject.AddComponent<MeshEditController>();
        
        // Set compute shaders via reflection or serialized fields
        // In a real implementation, you'd expose these properly
        SetPrivateField(_editController, "_sdfGeneratorShader", _sdfGenerator);
        SetPrivateField(_editController, "_sdfOperationsShader", _sdfOperations);
        SetPrivateField(_editController, "_marchingCubesShader", _marchingCubes);

        // Apply brush settings if available
        if (_brushSettings != null)
        {
            SetPrivateField(_editController, "_sdfResolution", _brushSettings.SDFResolution);
            SetPrivateField(_editController, "_boundsPadding", _brushSettings.BoundsPadding);
            SetPrivateField(_editController, "_useAsyncMeshGeneration", _brushSettings.UseAsyncMeshGeneration);
        }

        // Add cutting tool
        if (_enableCuttingTool)
        {
            _cuttingTool = gameObject.AddComponent<CuttingTool>();
            _cuttingTool.PreviewMaterial = _previewMaterial;
            SetPrivateField(_cuttingTool, "_editController", _editController);
            
            if (_brushSettings != null)
            {
                _cuttingTool.ToolType = _brushSettings.DefaultCutTool;
                _cuttingTool.ToolScale = _brushSettings.DefaultCutScale;
            }
        }

        // Add deform brush
        if (_enableDeformBrush)
        {
            _deformBrush = gameObject.AddComponent<DeformBrush>();
            SetPrivateField(_deformBrush, "_editController", _editController);
            
            if (_brushSettings != null)
            {
                _deformBrush.BrushRadius = _brushSettings.DeformRadius;
                _deformBrush.BrushStrength = _brushSettings.DeformStrength;
            }
        }

        // Add undo system
        if (_enableUndoSystem)
        {
            _undoSystem = gameObject.AddComponent<SDFUndoSystem>();
            SetPrivateField(_undoSystem, "_editController", _editController);
        }

        // Add LOD manager
        if (_enableLOD)
        {
            _lodManager = gameObject.AddComponent<SDFLODManager>();
            SetPrivateField(_lodManager, "_editController", _editController);
        }

        // Add operation queue
        if (_enableOperationQueue)
        {
            _operationQueue = gameObject.AddComponent<SDFOperationQueue>();
        }
    }

    private void InitializeEditor()
    {
        if (_editController != null)
        {
            _editController.Initialize();
            
            // Subscribe to events
            _editController.OnMeshUpdated += OnMeshUpdated;
            _editController.OnOperationComplete += OnOperationComplete;
        }
    }

    private void OnMeshUpdated(Mesh mesh)
    {
        Debug.Log($"Mesh updated: {mesh.vertexCount} vertices, {mesh.triangles.Length / 3} triangles");
    }

    private void OnOperationComplete()
    {
        // Could trigger effects, sounds, etc.
    }

    private void SetPrivateField(object obj, string fieldName, object value)
    {
        var field = obj.GetType().GetField(fieldName, 
            System.Reflection.BindingFlags.NonPublic | 
            System.Reflection.BindingFlags.Instance);
        
        if (field != null)
        {
            field.SetValue(obj, value);
        }
    }

    /// <summary>
    /// Switch to cutting mode.
    /// </summary>
    public void EnableCuttingMode()
    {
        if (_cuttingTool != null) _cuttingTool.IsEnabled = true;
        if (_deformBrush != null) _deformBrush.IsEnabled = false;
    }

    /// <summary>
    /// Switch to deform mode.
    /// </summary>
    public void EnableDeformMode()
    {
        if (_cuttingTool != null) _cuttingTool.IsEnabled = false;
        if (_deformBrush != null) _deformBrush.IsEnabled = true;
    }

    /// <summary>
    /// Disable all tools.
    /// </summary>
    public void DisableTools()
    {
        if (_cuttingTool != null) _cuttingTool.IsEnabled = false;
        if (_deformBrush != null) _deformBrush.IsEnabled = false;
    }

    /// <summary>
    /// Get the final edited mesh.
    /// </summary>
    public Mesh GetEditedMesh()
    {
        return _editController?.GetFinalMesh();
    }

    /// <summary>
    /// Reset to original mesh.
    /// </summary>
    public void ResetMesh()
    {
        _editController?.Reset();
        _undoSystem?.ClearHistory();
    }

    private void OnDestroy()
    {
        if (_editController != null)
        {
            _editController.OnMeshUpdated -= OnMeshUpdated;
            _editController.OnOperationComplete -= OnOperationComplete;
        }
    }
}