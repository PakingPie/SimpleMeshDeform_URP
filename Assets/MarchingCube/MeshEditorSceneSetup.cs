// Editor/MeshEditorSceneSetup.cs
#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public class MeshEditorSceneSetup : EditorWindow
{
    private ComputeShader _sdfGenerator;
    private ComputeShader _sdfOperations;
    private ComputeShader _marchingCubes;
    private Material _previewMaterial;
    private BrushSettings _brushSettings;
    private PrimitiveType _testMeshType = PrimitiveType.Cube;
    private Material _meshMaterial;

    [MenuItem("Tools/Mesh Editor/Create Test Scene")]
    public static void ShowWindow()
    {
        GetWindow<MeshEditorSceneSetup>("Mesh Editor Setup");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Mesh Editor Test Scene Setup", EditorStyles.boldLabel);
        EditorGUILayout.Space(10);

        EditorGUILayout.LabelField("Required Compute Shaders", EditorStyles.boldLabel);
        _sdfGenerator = (ComputeShader)EditorGUILayout.ObjectField("SDF Generator", _sdfGenerator, typeof(ComputeShader), false);
        _sdfOperations = (ComputeShader)EditorGUILayout.ObjectField("SDF Operations", _sdfOperations, typeof(ComputeShader), false);
        _marchingCubes = (ComputeShader)EditorGUILayout.ObjectField("Marching Cubes", _marchingCubes, typeof(ComputeShader), false);
        
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Cutting Tool Preview Material", EditorStyles.boldLabel);
        _previewMaterial = (Material)EditorGUILayout.ObjectField("Cutting Tool Preview Material", _previewMaterial, typeof(Material), false);
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Optional Settings", EditorStyles.boldLabel);
        _brushSettings = (BrushSettings)EditorGUILayout.ObjectField("Brush Settings", _brushSettings, typeof(BrushSettings), false);
        _meshMaterial = (Material)EditorGUILayout.ObjectField("Mesh Material", _meshMaterial, typeof(Material), false);
        _testMeshType = (PrimitiveType)EditorGUILayout.EnumPopup("Test Mesh Type", _testMeshType);

        EditorGUILayout.Space(10);

        // Auto-find button
        if (GUILayout.Button("Auto-Find Compute Shaders"))
        {
            AutoFindShaders();
        }

        EditorGUILayout.Space(5);

        // Validation
        bool isValid = _sdfGenerator != null && _sdfOperations != null && _marchingCubes != null && _previewMaterial != null;

        EditorGUI.BeginDisabledGroup(!isValid);
        if (GUILayout.Button("Create Test Scene", GUILayout.Height(40)))
        {
            CreateTestScene();
        }
        EditorGUI.EndDisabledGroup();

        if (!isValid)
        {
            EditorGUILayout.HelpBox("Please assign all compute shaders before creating the scene.", MessageType.Warning);
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Quick Setup (Current Scene)", EditorStyles.boldLabel);
        
        EditorGUI.BeginDisabledGroup(!isValid);
        if (GUILayout.Button("Add Editable Mesh to Scene"))
        {
            AddEditableMeshToScene();
        }
        EditorGUI.EndDisabledGroup();
    }

    private void AutoFindShaders()
    {
        string[] guids = AssetDatabase.FindAssets("t:ComputeShader");
        
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            
            if (shader.name.Contains("SDFGenerator") && _sdfGenerator == null)
                _sdfGenerator = shader;
            else if (shader.name.Contains("SDFOperations") && _sdfOperations == null)
                _sdfOperations = shader;
            else if (shader.name.Contains("MarchingCubes") && _marchingCubes == null)
                _marchingCubes = shader;
        }

        // Auto-find brush settings
        string[] settingsGuids = AssetDatabase.FindAssets("t:BrushSettings");
        if (settingsGuids.Length > 0 && _brushSettings == null)
        {
            string path = AssetDatabase.GUIDToAssetPath(settingsGuids[0]);
            _brushSettings = AssetDatabase.LoadAssetAtPath<BrushSettings>(path);
        }
    }

    private void CreateTestScene()
    {
        // Create new scene
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        // Setup camera
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            mainCam.transform.position = new Vector3(0, 2, -4);
            mainCam.transform.LookAt(Vector3.zero);
        }

        // Add editable mesh
        AddEditableMeshToScene();

        // Add ground plane
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = new Vector3(0, -1, 0);
        ground.transform.localScale = new Vector3(2, 1, 2);
        ground.layer = LayerMask.NameToLayer("Default");

        // Add directional light adjustment
        Light[] lights = FindObjectsOfType<Light>();
        foreach (var light in lights)
        {
            if (light.type == LightType.Directional)
            {
                light.transform.rotation = Quaternion.Euler(50, -30, 0);
                light.intensity = 1.2f;
            }
        }

        // Save scene
        string scenePath = "Assets/MeshEditor/Scenes/MeshEditorTest.unity";
        EnsureDirectoryExists(scenePath);
        EditorSceneManager.SaveScene(scene, scenePath);

        Debug.Log("Test scene created at: " + scenePath);
    }

    private void AddEditableMeshToScene()
    {
        // Create test mesh object
        GameObject meshObj = GameObject.CreatePrimitive(_testMeshType);
        meshObj.name = "EditableMesh";
        meshObj.transform.position = Vector3.zero;

        // Set material
        if (_meshMaterial != null)
        {
            meshObj.GetComponent<MeshRenderer>().material = _meshMaterial;
        }

        // Add MeshEditController
        MeshEditController controller = meshObj.AddComponent<MeshEditController>();
        
        // Set compute shaders via SerializedObject
        SerializedObject serializedController = new SerializedObject(controller);
        serializedController.FindProperty("_sdfGeneratorShader").objectReferenceValue = _sdfGenerator;
        serializedController.FindProperty("_sdfOperationsShader").objectReferenceValue = _sdfOperations;
        serializedController.FindProperty("_marchingCubesShader").objectReferenceValue = _marchingCubes;
        serializedController.FindProperty("_previewMaterial").objectReferenceValue = _previewMaterial;
        
        if (_brushSettings != null)
        {
            serializedController.FindProperty("_sdfResolution").vector3IntValue = _brushSettings.SDFResolution;
            serializedController.FindProperty("_boundsPadding").floatValue = _brushSettings.BoundsPadding;
            serializedController.FindProperty("_useAsyncMeshGeneration").boolValue = _brushSettings.UseAsyncMeshGeneration;
        }
        
        serializedController.ApplyModifiedProperties();

        // Add CuttingTool
        CuttingTool cuttingTool = meshObj.AddComponent<CuttingTool>();
        cuttingTool.PreviewMaterial = _previewMaterial;
        SerializedObject serializedCutting = new SerializedObject(cuttingTool);
        serializedCutting.FindProperty("_editController").objectReferenceValue = controller;
        serializedCutting.ApplyModifiedProperties();

        // Add DeformBrush
        DeformBrush deformBrush = meshObj.AddComponent<DeformBrush>();
        SerializedObject serializedDeform = new SerializedObject(deformBrush);
        serializedDeform.FindProperty("_editController").objectReferenceValue = controller;
        serializedDeform.ApplyModifiedProperties();

        // Add Undo System
        SDFUndoSystem undoSystem = meshObj.AddComponent<SDFUndoSystem>();
        SerializedObject serializedUndo = new SerializedObject(undoSystem);
        serializedUndo.FindProperty("_editController").objectReferenceValue = controller;
        serializedUndo.ApplyModifiedProperties();

        // Select the created object
        Selection.activeGameObject = meshObj;

        Debug.Log("Editable mesh added to scene. Enter Play Mode to test.");
    }

    private void EnsureDirectoryExists(string filePath)
    {
        string directory = System.IO.Path.GetDirectoryName(filePath);
        if (!System.IO.Directory.Exists(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
            AssetDatabase.Refresh();
        }
    }
}
#endif