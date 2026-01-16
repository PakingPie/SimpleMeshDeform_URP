using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Represents an object that can be modified by grinding or drilling tools.
/// Attach this to a GameObject with MeshFilter and MeshRenderer.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class GrindableObject : MonoBehaviour
{
    [Header("Welding Settings")]
    [Tooltip("Distance threshold for merging vertices after operations")]
    [SerializeField] private float weldThreshold = 0.001f;

    [Header("Bounds Protection")]
    [Tooltip("Prevent vertices from being moved outside the original mesh surface")]
    [SerializeField] private bool enforceOriginalBounds = true;

    [Tooltip("Use BVH for precise mesh surface bounds (slower but more accurate)")]
    [SerializeField] private bool useBVHBounds = true;

    [Header("Debug")]
    [SerializeField] private bool showAffectedVertices = false;
    [SerializeField] private bool showOriginalBounds = false;
    [SerializeField] private bool showBVHBounds = false;

    public VertexColorUpdater colorUpdater;

    private MeshFilter meshFilter;
    private Mesh workingMesh;
    private Mesh originalMesh;

    // Original mesh data for reset
    private Vector3[] originalVertices;
    private int[] originalTriangles;
    private Vector3[] originalNormals;
    private Vector2[] originalUVs;
    private Bounds originalLocalBounds;
    private Bounds originalWorldBounds;

    // BVH for mesh-accurate bounds
    private MeshBVH meshBVH;
    private LinearBVH linearBVH;

    // Working data
    private Vector3[] currentVertices;
    private int[] currentTriangles;

    // For debug visualization
    private List<Vector3> lastAffectedVertices = new List<Vector3>();

    public float WeldThreshold
    {
        get => weldThreshold;
        set => weldThreshold = Mathf.Max(0.0001f, value);
    }

    public int VertexCount => currentVertices?.Length ?? 0;
    public int TriangleCount => currentTriangles?.Length / 3 ?? 0;

    public Bounds OriginalLocalBounds => originalLocalBounds;

    public Bounds OriginalWorldBounds
    {
        get
        {
            Vector3 worldCenter = transform.TransformPoint(originalLocalBounds.center);
            Vector3 worldSize = Vector3.Scale(originalLocalBounds.size, transform.lossyScale);
            return new Bounds(worldCenter, worldSize);
        }
    }

    public bool EnforceOriginalBounds
    {
        get => enforceOriginalBounds;
        set => enforceOriginalBounds = value;
    }

    public bool UseBVHBounds
    {
        get => useBVHBounds;
        set => useBVHBounds = value;
    }

    public MeshBVH MeshBVH => meshBVH;
    public LinearBVH LinearBVH => linearBVH;

    private void Awake()
    {
        Initialize();
    }

    public void Initialize()
    {
        meshFilter = GetComponent<MeshFilter>();

        if (meshFilter.sharedMesh == null)
        {
            Debug.LogError($"GrindableObject on {gameObject.name} has no mesh assigned!");
            return;
        }

        originalMesh = meshFilter.sharedMesh;

        workingMesh = Instantiate(originalMesh);
        workingMesh.name = originalMesh.name + "_Working";
        meshFilter.mesh = workingMesh;

        originalVertices = originalMesh.vertices;
        originalTriangles = originalMesh.triangles;
        originalNormals = originalMesh.normals;
        originalUVs = originalMesh.uv;
        originalLocalBounds = originalMesh.bounds;

        originalWorldBounds = OriginalWorldBounds;

        currentVertices = workingMesh.vertices;
        currentTriangles = workingMesh.triangles;

        // Build BVH for mesh-accurate bounds
        if (useBVHBounds)
        {
            BuildBVH();
        }

        Debug.Log($"GrindableObject initialized: {VertexCount} vertices, {TriangleCount} triangles");
    }

    /// <summary>
    /// Builds or rebuilds the BVH from the original mesh.
    /// </summary>
    public void BuildBVH()
    {
        meshBVH = new MeshBVH();
        meshBVH.Build(originalMesh, transform);

        linearBVH = new LinearBVH();
        linearBVH.BuildFromBVH(meshBVH);

        Debug.Log("BVH built for mesh-accurate bounds");
    }

    public Vector3[] GetVertices() => currentVertices;

    public void SetVertices(Vector3[] vertices)
    {
        if (vertices == null || vertices.Length != currentVertices.Length)
        {
            Debug.LogError("Invalid vertex array");
            return;
        }

        currentVertices = vertices;
        UpdateMesh();
    }

    public int[] GetTriangles() => currentTriangles;

    public Vector3 LocalToWorld(Vector3 localPos) => transform.TransformPoint(localPos);

    public Vector3 WorldToLocal(Vector3 worldPos) => transform.InverseTransformPoint(worldPos);

    /// <summary>
    /// Clamps a position to the AABB bounds. This is the absolute boundary that vertices cannot escape.
    /// </summary>
    public Vector3 ClampToOriginalBounds(Vector3 worldPos)
    {
        Bounds bounds = OriginalWorldBounds;
        worldPos.x = Mathf.Clamp(worldPos.x, bounds.min.x, bounds.max.x);
        worldPos.y = Mathf.Clamp(worldPos.y, bounds.min.y, bounds.max.y);
        worldPos.z = Mathf.Clamp(worldPos.z, bounds.min.z, bounds.max.z);
        return worldPos;
    }

    /// <summary>
    /// Checks if a position is inside the original AABB bounds.
    /// </summary>
    public bool IsInsideOriginalBounds(Vector3 worldPos)
    {
        Bounds bounds = OriginalWorldBounds;
        return worldPos.x >= bounds.min.x && worldPos.x <= bounds.max.x &&
               worldPos.y >= bounds.min.y && worldPos.y <= bounds.max.y &&
               worldPos.z >= bounds.min.z && worldPos.z <= bounds.max.z;
    }

    /// <summary>
    /// Clamps a position to the original mesh surface.
    /// Uses a multi-tier approach:
    /// 1. Always clamp to AABB first (absolute boundary)
    /// 2. If BVH available, use raycast for precise surface clamping
    /// 3. If raycast misses, use closest point on mesh as fallback
    /// </summary>
    /// <param name="originalPos">Original vertex position before grinding</param>
    /// <param name="targetPos">Target position after grinding</param>
    /// <param name="grindDirection">Direction of grinding (normalized)</param>
    /// <returns>Position clamped to mesh surface</returns>
    public Vector3 ClampToMeshSurface(Vector3 originalPos, Vector3 targetPos, Vector3 grindDirection)
    {
        // Step 1: Always clamp to AABB first
        Vector3 clampedPos = ClampToOriginalBounds(targetPos);

        if (!useBVHBounds || meshBVH == null)
        {
            return clampedPos;
        }

        // Calculate movement
        Vector3 moveVector = clampedPos - originalPos;
        float moveDistance = moveVector.magnitude;

        if (moveDistance < 0.0001f)
        {
            return clampedPos;
        }

        Vector3 moveDir = moveVector / moveDistance;

        // KEY FIX: Offset ray origin to skip past the starting surface
        float skipDistance = 0.02f;
        Vector3 rayOrigin = originalPos + moveDir * skipDistance;

        if (meshBVH.Raycast(rayOrigin, moveDir, out Vector3 hitPoint, out float hitDistance))
        {
            float remainingDistance = moveDistance - skipDistance;

            if (remainingDistance > hitDistance)
            {
                // Target is beyond exit surface - clamp
                return ClampToOriginalBounds(hitPoint);
            }
        }

        // Allow movement
        return clampedPos;
    }

    /// <summary>
    /// Applies multi-directional grinding effect based on the grind tool.
    /// </summary>
    public int ApplyGrinding(GrindTool tool)
    {
        if (currentVertices == null || tool == null) return 0;

        Vector3 grindDirection = tool.GrindDirection;
        int affectedCount = 0;
        lastAffectedVertices.Clear();

        for (int i = 0; i < currentVertices.Length; i++)
        {
            Vector3 worldPos = LocalToWorld(currentVertices[i]);

            if (tool.ShouldGrindPoint(worldPos))
            {
                Vector3 targetPos = tool.GetGrindTargetPosition(worldPos);

                // Clamp to original mesh surface if enforcing bounds
                if (enforceOriginalBounds)
                {
                    targetPos = ClampToMeshSurface(worldPos, targetPos, grindDirection);
                }

                currentVertices[i] = WorldToLocal(targetPos);
                affectedCount++;

                if (colorUpdater != null)
                {
                    colorUpdater.UpdateVertexColor(i, targetPos);
                }

                if (showAffectedVertices)
                {
                    lastAffectedVertices.Add(targetPos);
                }
            }
        }

        if (affectedCount > 0)
        {
            UpdateMesh();

            if (colorUpdater != null)
            {
                colorUpdater.ApplyColors();
            }
        }

        return affectedCount;
    }

    /// <summary>
    /// Applies drilling effect based on the drill tool's cylinder.
    /// </summary>
    public int ApplyDrilling(DrillTool tool)
    {
        if (currentVertices == null || tool == null) return 0;

        Vector3 drillDirection = tool.DrillDirection;
        int affectedCount = 0;
        lastAffectedVertices.Clear();

        for (int i = 0; i < currentVertices.Length; i++)
        {
            Vector3 worldPos = LocalToWorld(currentVertices[i]);

            Vector3 projectedPos;
            bool wasProjected = false;

            if (enforceOriginalBounds)
            {
                // Get the raw projected position first
                projectedPos = tool.ProjectToSurface(worldPos, out wasProjected);

                if (wasProjected)
                {
                    // Use ClampToMeshSurface for consistent behavior with grinding
                    projectedPos = ClampToMeshSurface(worldPos, projectedPos, drillDirection);
                    Debug.Log($"Drill clamped vertex {i} to mesh surface at {projectedPos}");
                }
            }
            else
            {
                projectedPos = tool.ProjectToSurface(worldPos, out wasProjected);
            }

            if (wasProjected)
            {
                currentVertices[i] = WorldToLocal(projectedPos);
                affectedCount++;

                if (colorUpdater != null)
                {
                    colorUpdater.UpdateVertexColor(i, projectedPos);
                }

                if (showAffectedVertices)
                {
                    lastAffectedVertices.Add(projectedPos);
                }
            }
        }

        if (affectedCount > 0)
        {
            UpdateMesh();

            if (colorUpdater != null)
            {
                colorUpdater.ApplyColors();
            }
        }

        return affectedCount;
    }

    private void UpdateMesh()
    {
        workingMesh.vertices = currentVertices;
        workingMesh.triangles = currentTriangles;
        workingMesh.RecalculateNormals();
        workingMesh.RecalculateBounds();
    }

    public int WeldVertices()
    {
        if (currentVertices == null || currentVertices.Length == 0) return 0;

        Dictionary<int, int> vertexRemap = new Dictionary<int, int>();
        List<Vector3> newVertices = new List<Vector3>();
        List<Vector2> newUVs = new List<Vector2>();

        Vector2[] currentUVs = workingMesh.uv;
        bool hasUVs = currentUVs != null && currentUVs.Length == currentVertices.Length;

        float thresholdSqr = weldThreshold * weldThreshold;

        for (int i = 0; i < currentVertices.Length; i++)
        {
            Vector3 vertex = currentVertices[i];
            int foundIndex = -1;

            for (int j = 0; j < newVertices.Count; j++)
            {
                if ((newVertices[j] - vertex).sqrMagnitude < thresholdSqr)
                {
                    foundIndex = j;
                    break;
                }
            }

            if (foundIndex >= 0)
            {
                vertexRemap[i] = foundIndex;
            }
            else
            {
                vertexRemap[i] = newVertices.Count;
                newVertices.Add(vertex);
                if (hasUVs)
                {
                    newUVs.Add(currentUVs[i]);
                }
            }
        }

        int[] newTriangles = new int[currentTriangles.Length];
        for (int i = 0; i < currentTriangles.Length; i++)
        {
            newTriangles[i] = vertexRemap[currentTriangles[i]];
        }

        List<int> validTriangles = new List<int>();
        for (int i = 0; i < newTriangles.Length; i += 3)
        {
            int a = newTriangles[i];
            int b = newTriangles[i + 1];
            int c = newTriangles[i + 2];

            if (a != b && b != c && c != a)
            {
                validTriangles.Add(a);
                validTriangles.Add(b);
                validTriangles.Add(c);
            }
        }

        currentVertices = newVertices.ToArray();
        currentTriangles = validTriangles.ToArray();

        workingMesh.Clear();
        workingMesh.vertices = currentVertices;
        workingMesh.triangles = currentTriangles;
        if (hasUVs && newUVs.Count > 0)
        {
            workingMesh.uv = newUVs.ToArray();
        }
        workingMesh.RecalculateNormals();
        workingMesh.RecalculateBounds();

        int removedVertices = vertexRemap.Count - newVertices.Count;
        int removedTriangles = (newTriangles.Length - validTriangles.Count) / 3;

        Debug.Log($"Welding: Removed {removedVertices} vertices, {removedTriangles} degenerate triangles.");

        return currentVertices.Length;
    }

    public Mesh GetWorkingMesh() => workingMesh;

    public void ResetMesh()
    {
        if (originalVertices == null) return;

        currentVertices = (Vector3[])originalVertices.Clone();
        currentTriangles = (int[])originalTriangles.Clone();

        workingMesh.Clear();
        workingMesh.vertices = currentVertices;
        workingMesh.triangles = currentTriangles;
        if (originalUVs != null && originalUVs.Length > 0)
        {
            workingMesh.uv = originalUVs;
        }
        if (originalNormals != null && originalNormals.Length > 0)
        {
            workingMesh.normals = originalNormals;
        }
        else
        {
            workingMesh.RecalculateNormals();
        }
        workingMesh.RecalculateBounds();

        Debug.Log($"Mesh reset: {VertexCount} vertices, {TriangleCount} triangles");

        if (colorUpdater != null)
        {
            colorUpdater.InitVertexColors(workingMesh);
        }
    }

    private void OnDestroy()
    {
        if (workingMesh != null)
        {
            Destroy(workingMesh);
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (showAffectedVertices && lastAffectedVertices.Count > 0)
        {
            Gizmos.color = Color.red;
            foreach (var pos in lastAffectedVertices)
            {
                Gizmos.DrawSphere(pos, 0.002f);
            }
        }

        if (showOriginalBounds && Application.isPlaying)
        {
            Gizmos.color = new Color(0f, 0f, 1f, 0.3f);
            Bounds bounds = OriginalWorldBounds;
            Gizmos.DrawWireCube(bounds.center, bounds.size);
        }

        if (showBVHBounds && Application.isPlaying && meshBVH != null && meshBVH.Root != null)
        {
            DrawBVHNode(meshBVH.Root, 0);
        }
    }

    private void DrawBVHNode(MeshBVH.BVHNode node, int depth)
    {
        if (node == null) return;

        float hue = (depth * 0.1f) % 1f;
        Gizmos.color = Color.HSVToRGB(hue, 0.8f, 0.8f);
        Gizmos.DrawWireCube(node.bounds.center, node.bounds.size);

        if (!node.IsLeaf)
        {
            DrawBVHNode(node.left, depth + 1);
            DrawBVHNode(node.right, depth + 1);
        }
    }
}