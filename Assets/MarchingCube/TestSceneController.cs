// TestSceneController.cs
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// Runtime controller for testing the mesh editor.
/// Attach to a GameObject in your test scene.
/// </summary>
public class TestSceneController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MeshEditController _editController;
    [SerializeField] private CuttingTool _cuttingTool;
    [SerializeField] private DeformBrush _deformBrush;
    [SerializeField] private SDFUndoSystem _undoSystem;

    [Header("UI (Optional)")]
    [SerializeField] private Text _statusText;
    [SerializeField] private Text _helpText;

    private enum EditMode { None, Cut, Deform }
    private EditMode _currentMode = EditMode.Cut;

    private void Start()
    {
        // Auto-find components if not assigned
        if (_editController == null)
            _editController = FindAnyObjectByType<MeshEditController>();
        if (_cuttingTool == null)
            _cuttingTool = FindAnyObjectByType<CuttingTool>();
        if (_deformBrush == null)
            _deformBrush = FindAnyObjectByType<DeformBrush>();
        if (_undoSystem == null)
            _undoSystem = FindAnyObjectByType<SDFUndoSystem>();

        // Initialize controller
        if (_editController != null && !_editController.IsInitialized)
        {
            _editController.Initialize();
        }

        // Set initial mode
        SetMode(EditMode.Cut);

        UpdateHelpText();
    }

    private void Update()
    {
        HandleModeSwitch();
        HandleToolSwitch();
        UpdateStatusText();
    }

    private void HandleModeSwitch()
    {
        // Tab to switch between Cut and Deform
        if (Keyboard.current.tabKey.wasPressedThisFrame)
        {
            if (_currentMode == EditMode.Cut)
                SetMode(EditMode.Deform);
            else
                SetMode(EditMode.Cut);
        }

        // R to reset mesh
        if (Keyboard.current.rKey.wasPressedThisFrame)
        {
            ResetMesh();
        }

        // E to export mesh
        if (Keyboard.current.eKey.wasPressedThisFrame)
        {
            ExportMesh();
        }
    }

    private void HandleToolSwitch()
    {
        // These are handled by CuttingTool and DeformBrush internally
        // 1, 2, 3 keys switch tools/modes
        // Scroll wheel adjusts size
    }

    private void SetMode(EditMode mode)
    {
        _currentMode = mode;

        if (_cuttingTool != null)
            _cuttingTool.IsEnabled = (mode == EditMode.Cut);

        if (_deformBrush != null)
            _deformBrush.IsEnabled = (mode == EditMode.Deform);

        Debug.Log($"Switched to {mode} mode");
    }

    private void ResetMesh()
    {
        if (_editController != null)
        {
            _editController.Reset();
            Debug.Log("Mesh reset to original state");
        }

        if (_undoSystem != null)
        {
            _undoSystem.ClearHistory();
        }
    }

    private void ExportMesh()
    {
        if (_editController != null && _editController.IsInitialized)
        {
            Mesh finalMesh = _editController.GetFinalMesh();
            
            #if UNITY_EDITOR
            string path = UnityEditor.EditorUtility.SaveFilePanelInProject(
                "Export Mesh", "EditedMesh", "asset", "Save mesh");
            
            if (!string.IsNullOrEmpty(path))
            {
                UnityEditor.AssetDatabase.CreateAsset(finalMesh, path);
                UnityEditor.AssetDatabase.SaveAssets();
                Debug.Log($"Mesh exported to: {path}");
            }
            #else
            Debug.Log($"Mesh ready: {finalMesh.vertexCount} vertices, {finalMesh.triangles.Length / 3} triangles");
            #endif
        }
    }

    private void UpdateStatusText()
    {
        if (_statusText == null) return;

        string status = $"Mode: {_currentMode}\n";
        
        if (_editController != null && _editController.IsInitialized)
        {
            status += $"Vertices: {_editController.CurrentMesh?.vertexCount ?? 0}\n";
            status += $"Resolution: {_editController.SDFVolume?.Resolution}\n";
        }
        else
        {
            status += "Not Initialized\n";
        }

        if (_undoSystem != null)
        {
            status += $"Undo: {_undoSystem.UndoCount} | Redo: {_undoSystem.RedoCount}";
        }

        _statusText.text = status;
    }

    private void UpdateHelpText()
    {
        if (_helpText == null) return;

        _helpText.text = @"Controls:
Tab - Switch Cut/Deform mode
1/2/3 - Switch tool type
Scroll - Resize tool
Shift+Scroll - Adjust strength (Deform)
Click - Apply tool
Ctrl+Click - Invert (Push↔Pull)
Ctrl+Z - Undo
Ctrl+Y - Redo
R - Reset mesh
E - Export mesh";
    }

    private void OnGUI()
    {
        // Simple on-screen help if no UI canvas
        if (_helpText == null)
        {
            GUILayout.BeginArea(new Rect(10, 10, 250, 300));
            GUILayout.BeginVertical("box");
            
            GUILayout.Label($"Mode: {_currentMode}", GUI.skin.label);
            
            if (_editController != null && _editController.IsInitialized)
            {
                GUILayout.Label($"Vertices: {_editController.CurrentMesh?.vertexCount ?? 0}");
                GUILayout.Label($"Resolution: {_editController.SDFVolume?.Resolution}");
            }
            
            if (_undoSystem != null)
            {
                GUILayout.Label($"Undo: {_undoSystem.UndoCount} | Redo: {_undoSystem.RedoCount}");
            }
            
            GUILayout.Space(10);
            GUILayout.Label("Controls:", GUI.skin.label);
            GUILayout.Label("Tab - Switch mode");
            GUILayout.Label("1/2/3 - Tool type");
            GUILayout.Label("Scroll - Resize");
            GUILayout.Label("Click - Apply");
            GUILayout.Label("Ctrl+Z/Y - Undo/Redo");
            GUILayout.Label("R - Reset | E - Export");
            
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
    }
}