// BrushSettings.cs
using UnityEngine;

/// <summary>
/// Scriptable object for storing brush and tool settings.
/// </summary>
[CreateAssetMenu(fileName = "BrushSettings", menuName = "Mesh Editor/Brush Settings")]
public class BrushSettings : ScriptableObject
{
    [Header("Deformation Brush")]
    [Range(0.01f, 2f)]
    public float DeformRadius = 0.1f;
    
    [Range(0.001f, 0.1f)]
    public float DeformStrength = 0.01f;
    
    [Range(0.5f, 5f)]
    public float DeformFalloff = 2f;
    
    [Header("Cutting Tool")]
    public SDFOperations.ToolType DefaultCutTool = SDFOperations.ToolType.Sphere;
    public Vector3 DefaultCutScale = Vector3.one * 0.2f;
    
    [Range(0f, 0.1f)]
    public float CutBlendRadius = 0.02f;
    
    [Header("SDF Volume")]
    public Vector3Int SDFResolution = new Vector3Int(64, 64, 64);
    
    [Range(0.05f, 0.5f)]
    public float BoundsPadding = 0.1f;
    
    [Header("Performance")]
    public bool UseAsyncMeshGeneration = true;
    
    [Range(1, 30)]
    public int FramesBetweenMeshUpdates = 5;
    
    [Range(100000, 2000000)]
    public int MaxVertices = 500000;
    
    [Header("Island Separation")]
    public bool AutoSeparateIslands = false;
    
    [Range(0f, 0.01f)]
    public float MinIslandVolume = 0.0001f;
    
    [Header("UV Transfer")]
    [Range(0.01f, 0.5f)]
    public float UVSearchDistance = 0.1f;
    
    [Range(1f, 10f)]
    public float TriplanarBlendSharpness = 4f;
    
    public float TriplanarScale = 1f;
    
    /// <summary>
    /// Create default settings asset.
    /// </summary>
    public static BrushSettings CreateDefault()
    {
        return CreateInstance<BrushSettings>();
    }
}