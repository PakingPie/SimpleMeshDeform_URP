// Editor/CuttingToolEditor.cs
#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(CuttingTool))]
public class CuttingToolEditor : Editor
{
    private CuttingTool _tool;
    private SerializedProperty _toolType;
    private SerializedProperty _toolScale;
    private SerializedProperty _blendRadius;

    private void OnEnable()
    {
        _tool = (CuttingTool)target;
        _toolType = serializedObject.FindProperty("_toolType");
        _toolScale = serializedObject.FindProperty("_toolScale");
        _blendRadius = serializedObject.FindProperty("_blendRadius");
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Tool Preview", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            DrawToolButton(SDFOperations.ToolType.Sphere, "Sphere (1)");
            DrawToolButton(SDFOperations.ToolType.Box, "Box (2)");
            DrawToolButton(SDFOperations.ToolType.Cylinder, "Cylinder (3)");
        }

        EditorGUILayout.Space(5);
        EditorGUILayout.HelpBox(
            "Controls:\n" +
            "• Click to cut\n" +
            "• Scroll to resize\n" +
            "• Hold Shift + click for surface-aligned cuts\n" +
            "• 1/2/3 keys to switch tools",
            MessageType.Info
        );
    }

    private void DrawToolButton(SDFOperations.ToolType type, string label)
    {
        bool isSelected = (SDFOperations.ToolType)_toolType.enumValueIndex == type;
        
        GUI.color = isSelected ? Color.cyan : Color.white;
        
        if (GUILayout.Button(label, GUILayout.Height(30)))
        {
            _toolType.enumValueIndex = (int)type;
            serializedObject.ApplyModifiedProperties();
        }
        
        GUI.color = Color.white;
    }

    private void OnSceneGUI()
    {
        if (!Application.isPlaying) return;

        // Draw tool size handles
        Handles.color = new Color(1f, 0.5f, 0f, 0.5f);
        
        Vector3 toolPos = _tool.transform.position;
        Vector3 toolScale = _toolScale.vector3Value;
        
        EditorGUI.BeginChangeCheck();
        
        float handleSize = HandleUtility.GetHandleSize(toolPos) * 0.1f;
        
        // Scale handles
        Vector3 newScale = Handles.ScaleHandle(toolScale, toolPos, Quaternion.identity, handleSize * 10f);
        
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(_tool, "Change Tool Scale");
            _toolScale.vector3Value = newScale;
            serializedObject.ApplyModifiedProperties();
        }
    }
}

[CustomEditor(typeof(DeformBrush))]
public class DeformBrushEditor : Editor
{
    private DeformBrush _brush;
    private SerializedProperty _brushMode;
    private SerializedProperty _brushRadius;
    private SerializedProperty _brushStrength;
    private SerializedProperty _brushFalloff;

    private void OnEnable()
    {
        _brush = (DeformBrush)target;
        _brushMode = serializedObject.FindProperty("_brushMode");
        _brushRadius = serializedObject.FindProperty("_brushRadius");
        _brushStrength = serializedObject.FindProperty("_brushStrength");
        _brushFalloff = serializedObject.FindProperty("_brushFalloff");
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Brush Mode", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            DrawModeButton(SDFOperations.DeformationType.Push, "Push (1)", Color.blue);
            DrawModeButton(SDFOperations.DeformationType.Pull, "Pull (2)", new Color(1f, 0.5f, 0f));
            DrawModeButton(SDFOperations.DeformationType.Smooth, "Smooth (3)", Color.green);
        }

        EditorGUILayout.Space(5);

        // Brush preview
        DrawBrushPreview();

        EditorGUILayout.Space(5);
        EditorGUILayout.HelpBox(
            "Controls:\n" +
            "• Click and drag to deform\n" +
            "• Scroll to resize brush\n" +
            "• Shift + Scroll to adjust strength\n" +
            "• Ctrl to invert push/pull\n" +
            "• 1/2/3 keys to switch modes",
            MessageType.Info
        );
    }

    private void DrawModeButton(SDFOperations.DeformationType mode, string label, Color color)
    {
        bool isSelected = (SDFOperations.DeformationType)_brushMode.enumValueIndex == mode;
        
        GUI.color = isSelected ? color : Color.white;
        
        if (GUILayout.Button(label, GUILayout.Height(30)))
        {
            _brushMode.enumValueIndex = (int)mode;
            serializedObject.ApplyModifiedProperties();
        }
        
        GUI.color = Color.white;
    }

    private void DrawBrushPreview()
    {
        Rect rect = GUILayoutUtility.GetRect(100, 60);
        
        if (Event.current.type == EventType.Repaint)
        {
            // Draw falloff curve preview
            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f));
            
            float falloff = _brushFalloff.floatValue;
            
            Handles.BeginGUI();
            Handles.color = Color.cyan;
            
            Vector3 prevPoint = Vector3.zero;
            for (int i = 0; i <= 50; i++)
            {
                float t = i / 50f;
                float value = Mathf.Pow(1f - t, falloff);
                
                Vector3 point = new Vector3(
                    rect.x + t * rect.width,
                    rect.y + rect.height - value * rect.height,
                    0
                );
                
                if (i > 0)
                {
                    Handles.DrawLine(prevPoint, point);
                }
                
                prevPoint = point;
            }
            
            Handles.EndGUI();
        }
        
        EditorGUILayout.LabelField("Falloff Curve", EditorStyles.centeredGreyMiniLabel);
    }

    private void OnSceneGUI()
    {
        if (!Application.isPlaying) return;

        // Draw brush radius visualization
        Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        
        if (Physics.Raycast(ray, out RaycastHit hit, 100f))
        {
            float radius = _brushRadius.floatValue;
            
            // Draw brush circle
            Handles.color = GetBrushColor();
            Handles.DrawWireDisc(hit.point, hit.normal, radius);
            
            // Draw falloff rings
            Handles.color = new Color(Handles.color.r, Handles.color.g, Handles.color.b, 0.3f);
            Handles.DrawWireDisc(hit.point, hit.normal, radius * 0.66f);
            Handles.DrawWireDisc(hit.point, hit.normal, radius * 0.33f);
            
            SceneView.RepaintAll();
        }
    }

    private Color GetBrushColor()
    {
        return (SDFOperations.DeformationType)_brushMode.enumValueIndex switch
        {
            SDFOperations.DeformationType.Push => new Color(0f, 0.5f, 1f, 0.8f),
            SDFOperations.DeformationType.Pull => new Color(1f, 0.5f, 0f, 0.8f),
            SDFOperations.DeformationType.Smooth => new Color(0f, 1f, 0.5f, 0.8f),
            _ => Color.white
        };
    }
}
#endif