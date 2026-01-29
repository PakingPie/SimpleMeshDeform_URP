using UnityEngine;
using UnityEngine.Rendering;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif
public class SDFVolumeTest : MonoBehaviour
{
    [Header("SDF Volume Settings")]
    public Vector3Int Resolution = new Vector3Int(64, 64, 64);
    public Bounds WorldBounds;

    [Header("Origin Mesh")]
    public GameObject SourceMeshObject;
    [Header("Compute Shaders")]
    public ComputeShader SDFGeneratorShader;

    public float VoxelSize => (WorldBounds.size.x / Resolution.x +
                            WorldBounds.size.y / Resolution.y +
                            WorldBounds.size.z / Resolution.z) / 3f;

    private RenderTexture _sdfTex;
    private ComputeBuffer _bvhNodeBuffer;
    private ComputeBuffer _triangleBuffer;
    private int _initializeKernel;
    private int _meshToSDFKernel;
    private int _finalizeKernel;

    private MeshBVH _meshBVH;
    private LinearBVH _linearBVH;

    public void Run()
    {
        InitializeSDFVolume();
        GenerateSDFVolume();
        FinalizeSDFVolume();
    }

    public void InitializeSDFVolume()
    {
        Bounds bounds = SourceMeshObject.GetComponent<MeshFilter>().sharedMesh.bounds;
        Vector3 padding = Vector3.one * 0.1f;
        bounds.Expand(padding * 2);
        WorldBounds = TransformBounds(bounds, SourceMeshObject.transform.localToWorldMatrix);

        CreateSDFTexture();
        _initializeKernel = SDFGeneratorShader.FindKernel("InitializeSDF");
        _meshToSDFKernel = SDFGeneratorShader.FindKernel("MeshToSDF");
        _finalizeKernel = SDFGeneratorShader.FindKernel("FinalizeSDF");

        SetCommonParameters(SDFGeneratorShader);
        SDFGeneratorShader.SetFloat("_InitialValue", 1f);
        SDFGeneratorShader.SetTexture(_initializeKernel, "_SDFVolume", _sdfTex);

        DispatchCompute(SDFGeneratorShader, _initializeKernel);
    }

    private void GenerateSDFVolume()
    {
        _meshBVH = new MeshBVH();
        _meshBVH.Build(SourceMeshObject.GetComponent<MeshFilter>().sharedMesh, transform);
        _linearBVH = new LinearBVH();
        _linearBVH.BuildFromBVH(_meshBVH);

        _bvhNodeBuffer = null;
        _triangleBuffer = null;

        _linearBVH.CreateBuffers(out _bvhNodeBuffer, out _triangleBuffer);
        GenerateSDFFromMesh(_bvhNodeBuffer, _triangleBuffer, _linearBVH.NodeCount);
    }

    private void GenerateSDFFromMesh(ComputeBuffer bvhNodesBuffer, ComputeBuffer trianglesBuffer,
                                  int nodeCount, float narrowBandWidth = -1f)
    {
        if (SDFGeneratorShader == null) return;

        if (narrowBandWidth < 0)
            narrowBandWidth = VoxelSize * 5f; // Default: 5 voxels

        SetCommonParameters(SDFGeneratorShader);
        SDFGeneratorShader.SetBuffer(_meshToSDFKernel, "_BVHNodes", bvhNodesBuffer);
        SDFGeneratorShader.SetBuffer(_meshToSDFKernel, "_Triangles", trianglesBuffer);
        SDFGeneratorShader.SetInt("_BVHNodeCount", nodeCount);
        SDFGeneratorShader.SetFloat("_NarrowBandWidth", narrowBandWidth);
        SDFGeneratorShader.SetTexture(_meshToSDFKernel, "_SDFVolume", _sdfTex);

        DispatchCompute(SDFGeneratorShader, _meshToSDFKernel);
    }

    public void FinalizeSDFVolume()
    {
        if (SDFGeneratorShader == null) return;

        SetCommonParameters(SDFGeneratorShader);
        SDFGeneratorShader.SetTexture(_finalizeKernel, "_SDFVolume", _sdfTex);

        DispatchCompute(SDFGeneratorShader, _finalizeKernel);
    }

    private void CreateSDFTexture()
    {
        if (_sdfTex != null)
        {
            _sdfTex.Release();
        }

        _sdfTex = new RenderTexture(Resolution.x, Resolution.y, 0, RenderTextureFormat.RFloat)
        {
            dimension = TextureDimension.Tex3D,
            volumeDepth = Resolution.z,
            enableRandomWrite = true,
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "SDFVolume"
        };
        _sdfTex.Create();
    }

    private void SetCommonParameters(ComputeShader shader)
    {
        shader.SetVector("_VolumeMin", WorldBounds.min);
        shader.SetVector("_VolumeMax", WorldBounds.max);
        shader.SetInts("_VolumeResolution", Resolution.x, Resolution.y, Resolution.z);
    }

    private void DispatchCompute(ComputeShader shader, int kernel)
    {
        int threadGroupsX = Mathf.CeilToInt(Resolution.x / 8f);
        int threadGroupsY = Mathf.CeilToInt(Resolution.y / 8f);
        int threadGroupsZ = Mathf.CeilToInt(Resolution.z / 8f);

        shader.Dispatch(kernel, threadGroupsX, threadGroupsY, threadGroupsZ);
    }

    private Bounds TransformBounds(Bounds localBounds, Matrix4x4 matrix)
    {
        Vector3[] corners = new Vector3[8];
        Vector3 min = localBounds.min;
        Vector3 max = localBounds.max;

        corners[0] = matrix.MultiplyPoint3x4(new Vector3(min.x, min.y, min.z));
        corners[1] = matrix.MultiplyPoint3x4(new Vector3(max.x, min.y, min.z));
        corners[2] = matrix.MultiplyPoint3x4(new Vector3(min.x, max.y, min.z));
        corners[3] = matrix.MultiplyPoint3x4(new Vector3(max.x, max.y, min.z));
        corners[4] = matrix.MultiplyPoint3x4(new Vector3(min.x, min.y, max.z));
        corners[5] = matrix.MultiplyPoint3x4(new Vector3(max.x, min.y, max.z));
        corners[6] = matrix.MultiplyPoint3x4(new Vector3(min.x, max.y, max.z));
        corners[7] = matrix.MultiplyPoint3x4(new Vector3(max.x, max.y, max.z));

        Bounds result = new Bounds(corners[0], Vector3.zero);
        for (int i = 1; i < 8; i++)
        {
            result.Encapsulate(corners[i]);
        }

        return result;
    }

    public void SaveSDFVolumeDataAsset()
    {
#if UNITY_EDITOR
        if (_sdfTex != null)
        {
            string assetPath = $"Assets/Resources/SDFVolumeData.asset";
            AssetDatabase.CreateAsset(_sdfTex, assetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"SDF Volume data saved to {assetPath}");
        }
        else
        {
            Debug.LogWarning("No SDF texture to save.");
        }
#endif
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(SDFVolumeTest))]
public class SDFVolumeTestEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        SDFVolumeTest sdfVolumeTest = (SDFVolumeTest)target;
        if(GUILayout.Button("Generate SDF Volume"))
        {
            sdfVolumeTest.Run();
        }

        if (GUILayout.Button("Save SDF Volume Data Asset"))
        {
            sdfVolumeTest.SaveSDFVolumeDataAsset();
        }
    }
}
#endif