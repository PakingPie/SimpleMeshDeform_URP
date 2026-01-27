// UVTransfer.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Transfers UVs from original mesh to new mesh using BVH nearest-point lookup.
/// Falls back to triplanar projection for new surfaces.
/// </summary>
public class UVTransfer
{
    /// <summary>
    /// UV data from original mesh for transfer.
    /// </summary>
    public struct OriginalMeshData
    {
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector2[] UVs;
        public int[] Triangles;
        
        // BVH for fast lookup (using your existing implementation)
        public object BVH; // Replace with your LinearBVH type
    }

    /// <summary>
    /// Settings for UV transfer.
    /// </summary>
    public struct TransferSettings
    {
        public float MaxSearchDistance;
        public float TriplanarBlendSharpness;
        public float TriplanarScale;
        public bool UseTriplanarForNewSurfaces;

        public static TransferSettings Default => new TransferSettings
        {
            MaxSearchDistance = 0.1f,
            TriplanarBlendSharpness = 4f,
            TriplanarScale = 1f,
            UseTriplanarForNewSurfaces = true
        };
    }

    private OriginalMeshData _originalData;
    private TransferSettings _settings;

    public UVTransfer(Mesh originalMesh, TransferSettings settings = default)
    {
        if (settings.Equals(default(TransferSettings)))
        {
            settings = TransferSettings.Default;
        }
        _settings = settings;
        
        CacheOriginalMeshData(originalMesh);
    }

    private void CacheOriginalMeshData(Mesh mesh)
    {
        _originalData = new OriginalMeshData
        {
            Vertices = mesh.vertices,
            Normals = mesh.normals,
            UVs = mesh.uv,
            Triangles = mesh.triangles
        };

        // Build BVH from original mesh for fast lookup
        // This would use your existing LinearBVH implementation
        // _originalData.BVH = new LinearBVH(mesh);
    }

    /// <summary>
    /// Transfer UVs from original mesh to new mesh.
    /// </summary>
    public void TransferUVs(Mesh newMesh)
    {
        Vector3[] newVertices = newMesh.vertices;
        Vector3[] newNormals = newMesh.normals;
        Vector2[] newUVs = new Vector2[newVertices.Length];

        for (int i = 0; i < newVertices.Length; i++)
        {
            Vector3 position = newVertices[i];
            Vector3 normal = newNormals[i];

            // Try to find closest point on original mesh
            if (TryGetClosestUV(position, out Vector2 uv, out float distance))
            {
                if (distance < _settings.MaxSearchDistance)
                {
                    newUVs[i] = uv;
                    continue;
                }
            }

            // Fall back to triplanar projection for new surfaces
            if (_settings.UseTriplanarForNewSurfaces)
            {
                newUVs[i] = CalculateTriplanarUV(position, normal);
            }
        }

        newMesh.uv = newUVs;
    }

    /// <summary>
    /// Transfer UVs using compute shader (faster for large meshes).
    /// </summary>
    public void TransferUVsGPU(Mesh newMesh, ComputeShader transferShader, 
                               ComputeBuffer bvhNodesBuffer, ComputeBuffer trianglesBuffer)
    {
        // This would dispatch a compute shader that:
        // 1. For each new vertex, query BVH for closest point
        // 2. Interpolate UV from closest triangle
        // 3. Fall back to triplanar if distance > threshold
        
        // Implementation would be similar to SDFGenerator but outputs UVs
        Debug.LogWarning("GPU UV transfer not yet implemented. Using CPU fallback.");
        TransferUVs(newMesh);
    }

    private bool TryGetClosestUV(Vector3 position, out Vector2 uv, out float distance)
    {
        uv = Vector2.zero;
        distance = float.MaxValue;

        // Use BVH to find closest triangle
        // This is a simplified version - use your LinearBVH.Query method
        
        int closestTriangle = -1;
        Vector3 closestPoint = Vector3.zero;
        float closestBary1 = 0, closestBary2 = 0, closestBary3 = 0;

        // Brute force fallback (replace with BVH query)
        for (int t = 0; t < _originalData.Triangles.Length; t += 3)
        {
            int i0 = _originalData.Triangles[t];
            int i1 = _originalData.Triangles[t + 1];
            int i2 = _originalData.Triangles[t + 2];

            Vector3 v0 = _originalData.Vertices[i0];
            Vector3 v1 = _originalData.Vertices[i1];
            Vector3 v2 = _originalData.Vertices[i2];

            Vector3 closest = ClosestPointOnTriangle(position, v0, v1, v2, 
                out float bary1, out float bary2, out float bary3);
            float dist = Vector3.Distance(position, closest);

            if (dist < distance)
            {
                distance = dist;
                closestTriangle = t;
                closestPoint = closest;
                closestBary1 = bary1;
                closestBary2 = bary2;
                closestBary3 = bary3;
            }
        }

        if (closestTriangle >= 0)
        {
            // Interpolate UV using barycentric coordinates
            int i0 = _originalData.Triangles[closestTriangle];
            int i1 = _originalData.Triangles[closestTriangle + 1];
            int i2 = _originalData.Triangles[closestTriangle + 2];

            if (_originalData.UVs != null && _originalData.UVs.Length > 0)
            {
                Vector2 uv0 = _originalData.UVs[i0];
                Vector2 uv1 = _originalData.UVs[i1];
                Vector2 uv2 = _originalData.UVs[i2];

                uv = uv0 * closestBary1 + uv1 * closestBary2 + uv2 * closestBary3;
                return true;
            }
        }

        return false;
    }

    private Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c,
        out float baryA, out float baryB, out float baryC)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 ap = p - a;

        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        
        if (d1 <= 0f && d2 <= 0f)
        {
            baryA = 1f; baryB = 0f; baryC = 0f;
            return a;
        }

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        
        if (d3 >= 0f && d4 <= d3)
        {
            baryA = 0f; baryB = 1f; baryC = 0f;
            return b;
        }

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float v = d1 / (d1 - d3);
            baryA = 1f - v; baryB = v; baryC = 0f;
            return a + v * ab;
        }

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        
        if (d6 >= 0f && d5 <= d6)
        {
            baryA = 0f; baryB = 0f; baryC = 1f;
            return c;
        }

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float w = d2 / (d2 - d6);
            baryA = 1f - w; baryB = 0f; baryC = w;
            return a + w * ac;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            baryA = 0f; baryB = 1f - w; baryC = w;
            return b + w * (c - b);
        }

        float denom = 1f / (va + vb + vc);
        float v2 = vb * denom;
        float w2 = vc * denom;
        baryA = 1f - v2 - w2;
        baryB = v2;
        baryC = w2;
        
        return a + ab * v2 + ac * w2;
    }

    private Vector2 CalculateTriplanarUV(Vector3 position, Vector3 normal)
    {
        // Calculate blend weights based on normal direction
        Vector3 blendWeights = new Vector3(
            Mathf.Pow(Mathf.Abs(normal.x), _settings.TriplanarBlendSharpness),
            Mathf.Pow(Mathf.Abs(normal.y), _settings.TriplanarBlendSharpness),
            Mathf.Pow(Mathf.Abs(normal.z), _settings.TriplanarBlendSharpness)
        );
        
        // Normalize weights
        float sum = blendWeights.x + blendWeights.y + blendWeights.z;
        blendWeights /= sum;

        // Calculate UV for each projection plane
        Vector2 uvX = new Vector2(position.z, position.y) * _settings.TriplanarScale;
        Vector2 uvY = new Vector2(position.x, position.z) * _settings.TriplanarScale;
        Vector2 uvZ = new Vector2(position.x, position.y) * _settings.TriplanarScale;

        // Blend UVs based on weights
        Vector2 finalUV = uvX * blendWeights.x + uvY * blendWeights.y + uvZ * blendWeights.z;
        
        return finalUV;
    }

    /// <summary>
    /// Update the original mesh data (call after applying changes).
    /// </summary>
    public void UpdateOriginalMesh(Mesh mesh)
    {
        CacheOriginalMeshData(mesh);
    }
}