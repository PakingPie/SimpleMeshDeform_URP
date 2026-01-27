// Editor/MeshEditControllerEditor.cs
#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(MeshEditController))]
public class MeshEditControllerEditor : Editor
{
    private MeshEditController _controller;
    
    private SerializedProperty _sdfGeneratorShader;
    private SerializedProperty _sdfOperationsShader;
    private SerializedProperty _marchingCubesShader;
    private SerializedProperty _sdfResolution;
    private SerializedProperty _boundsPadding;
    private SerializedProperty _useAsyncMeshGeneration;
    private SerializedProperty _showSDFBounds;

    private bool _showDebugInfo = false;
    private bool _showQuickActions = true;

    private void OnEnable()
    {
        _controller = (MeshEditController)target;
        
        _sdfGeneratorShader = serializedObject.FindProperty("_sdfGeneratorShader");
        _sdfOperationsShader = serializedObject.FindProperty("_sdfOperationsShader");
        _marchingCubesShader = serializedObject.FindProperty("_marchingCubesShader");
        _sdfResolution = serializedObject.FindProperty("_sdfResolution");
        _boundsPadding = serializedObject.FindProperty("_boundsPadding");
        _useAsyncMeshGeneration = serializedObject.FindProperty("_useAsyncMeshGeneration");
        _showSDFBounds = serializedObject.FindProperty("_showSDFBounds");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawHeader();
        DrawComputeShaders();
        DrawSettings();
        DrawQuickActions();
        DrawDebugInfo();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawHeader()
    {
        EditorGUILayout.Space(5);
        
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            
            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter
            };
            
            EditorGUILayout.LabelField("SDF Mesh Editor", titleStyle, GUILayout.Height(24));
            
            GUILayout.FlexibleSpace();
        }

        // Status indicator
        Color statusColor = _controller.IsInitialized ? Color.green : Color.yellow;
        string statusText = _controller.IsInitialized ? "Initialized" : "Not Initialized";
        
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            
            GUI.color = statusColor;
            EditorGUILayout.LabelField("●", GUILayout.Width(15));
            GUI.color = Color.white;
            
            EditorGUILayout.LabelField(statusText, GUILayout.Width(100));
            
            GUILayout.FlexibleSpace();
        }

        EditorGUILayout.Space(10);
    }

    private void DrawComputeShaders()
    {
        EditorGUILayout.LabelField("Compute Shaders", EditorStyles.boldLabel);
        
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.PropertyField(_sdfGeneratorShader, new GUIContent("SDF Generator"));
            EditorGUILayout.PropertyField(_sdfOperationsShader, new GUIContent("SDF Operations"));
            EditorGUILayout.PropertyField(_marchingCubesShader, new GUIContent("Marching Cubes"));

            // Validation
            bool allAssigned = _sdfGeneratorShader.objectReferenceValue != null &&
                              _sdfOperationsShader.objectReferenceValue != null &&
                              _marchingCubesShader.objectReferenceValue != null;

            if (!allAssigned)
            {
                EditorGUILayout.HelpBox("All compute shaders must be assigned for the editor to function.", MessageType.Warning);
                
                if (GUILayout.Button("Auto-Find Shaders"))
                {
                    AutoFindShaders();
                }
            }
        }

        EditorGUILayout.Space(5);
    }

    private void DrawSettings()
    {
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            // Resolution with presets
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_sdfResolution, new GUIContent("SDF Resolution"));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(" ");
            
            if (GUILayout.Button("32³", EditorStyles.miniButtonLeft))
            {
                _sdfResolution.vector3IntValue = new Vector3Int(32, 32, 32);
            }
            if (GUILayout.Button("64³", EditorStyles.miniButtonMid))
            {
                _sdfResolution.vector3IntValue = new Vector3Int(64, 64, 64);
            }
            if (GUILayout.Button("128³", EditorStyles.miniButtonMid))
            {
                _sdfResolution.vector3IntValue = new Vector3Int(128, 128, 128);
            }
            if (GUILayout.Button("256³", EditorStyles.miniButtonRight))
            {
                _sdfResolution.vector3IntValue = new Vector3Int(256, 256, 256);
            }
            
            EditorGUILayout.EndHorizontal();

            // Memory estimate
            Vector3Int res = _sdfResolution.vector3IntValue;
            float memoryMB = (res.x * res.y * res.z * 4f) / (1024f * 1024f);
            EditorGUILayout.HelpBox($"Estimated GPU memory: {memoryMB:F1} MB", MessageType.Info);

            EditorGUILayout.Space(5);
            
            EditorGUILayout.PropertyField(_boundsPadding, new GUIContent("Bounds Padding"));
            EditorGUILayout.PropertyField(_useAsyncMeshGeneration, new GUIContent("Async Mesh Generation"));
            EditorGUILayout.PropertyField(_showSDFBounds, new GUIContent("Show SDF Bounds"));
        }

        EditorGUILayout.Space(5);
    }

    private void DrawQuickActions()
    {
        _showQuickActions = EditorGUILayout.Foldout(_showQuickActions, "Quick Actions", true);
        
        if (_showQuickActions)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginDisabledGroup(!Application.isPlaying);
                
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Initialize", GUILayout.Height(30)))
                    {
                        _controller.Initialize();
                    }
                    
                    if (GUILayout.Button("Reset", GUILayout.Height(30)))
                    {
                        _controller.Reset();
                    }
                }

                EditorGUILayout.Space(5);

                if (GUILayout.Button("Export Mesh", GUILayout.Height(25)))
                {
                    ExportMesh();
                }

                EditorGUI.EndDisabledGroup();

                if (!Application.isPlaying)
                {
                    EditorGUILayout.HelpBox("Enter Play Mode to use quick actions.", MessageType.Info);
                }
            }
        }

        EditorGUILayout.Space(5);
    }

    private void DrawDebugInfo()
    {
        _showDebugInfo = EditorGUILayout.Foldout(_showDebugInfo, "Debug Info", true);
        
        if (_showDebugInfo && _controller.IsInitialized)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginDisabledGroup(true);
                
                if (_controller.SDFVolume != null)
                {
                    EditorGUILayout.Vector3IntField("Volume Resolution", _controller.SDFVolume.Resolution);
                    EditorGUILayout.BoundsField("World Bounds", _controller.SDFVolume.WorldBounds);
                    EditorGUILayout.FloatField("Voxel Size", _controller.SDFVolume.VoxelSize);
                }

                if (_controller.CurrentMesh != null)
                {
                    EditorGUILayout.IntField("Vertex Count", _controller.CurrentMesh.vertexCount);
                    EditorGUILayout.IntField("Triangle Count", _controller.CurrentMesh.triangles.Length / 3);
                }
                
                EditorGUI.EndDisabledGroup();
            }
        }
    }

    private void AutoFindShaders()
    {
        string[] guids = AssetDatabase.FindAssets("t:ComputeShader");
        
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            
            if (shader.name.Contains("SDFGenerator") && _sdfGeneratorShader.objectReferenceValue == null)
            {
                _sdfGeneratorShader.objectReferenceValue = shader;
            }
            else if (shader.name.Contains("SDFOperations") && _sdfOperationsShader.objectReferenceValue == null)
            {
                _sdfOperationsShader.objectReferenceValue = shader;
            }
            else if (shader.name.Contains("MarchingCubes") && _marchingCubesShader.objectReferenceValue == null)
            {
                _marchingCubesShader.objectReferenceValue = shader;
            }
        }
    }

    private void ExportMesh()
    {
        if (_controller.CurrentMesh == null) return;

        string path = EditorUtility.SaveFilePanelInProject(
            "Export Mesh",
            _controller.CurrentMesh.name,
            "asset",
            "Save the edited mesh as an asset"
        );

        if (!string.IsNullOrEmpty(path))
        {
            Mesh meshCopy = Object.Instantiate(_controller.CurrentMesh);
            meshCopy.name = System.IO.Path.GetFileNameWithoutExtension(path);
            
            AssetDatabase.CreateAsset(meshCopy, path);
            AssetDatabase.SaveAssets();
            
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = meshCopy;
        }
    }
}
#endif