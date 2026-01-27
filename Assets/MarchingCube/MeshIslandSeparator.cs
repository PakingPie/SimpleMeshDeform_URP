// MeshIslandSeparator.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Detects and separates disconnected mesh islands after cutting operations.
/// </summary>
public class MeshIslandSeparator
{
    /// <summary>
    /// Represents a connected mesh island.
    /// </summary>
    public class MeshIsland
    {
        public List<int> TriangleIndices = new List<int>();
        public Bounds Bounds;
        public float Volume;
        public Vector3 Centroid;
    }

    /// <summary>
    /// Find all disconnected islands in a mesh.
    /// </summary>
    public static List<MeshIsland> FindIslands(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        
        // Build adjacency graph
        Dictionary<int, HashSet<int>> vertexToTriangles = new Dictionary<int, HashSet<int>>();
        Dictionary<int, HashSet<int>> triangleAdjacency = new Dictionary<int, HashSet<int>>();

        // Map vertices to triangles
        for (int t = 0; t < triangles.Length; t += 3)
        {
            int triIndex = t / 3;
            
            for (int i = 0; i < 3; i++)
            {
                int vertIndex = triangles[t + i];
                
                if (!vertexToTriangles.ContainsKey(vertIndex))
                {
                    vertexToTriangles[vertIndex] = new HashSet<int>();
                }
                vertexToTriangles[vertIndex].Add(triIndex);
            }
            
            triangleAdjacency[triIndex] = new HashSet<int>();
        }

        // Build triangle adjacency (triangles sharing vertices are adjacent)
        for (int t = 0; t < triangles.Length; t += 3)
        {
            int triIndex = t / 3;
            
            for (int i = 0; i < 3; i++)
            {
                int vertIndex = triangles[t + i];
                
                foreach (int adjacentTri in vertexToTriangles[vertIndex])
                {
                    if (adjacentTri != triIndex)
                    {
                        triangleAdjacency[triIndex].Add(adjacentTri);
                    }
                }
            }
        }

        // Find connected components using flood fill
        HashSet<int> visited = new HashSet<int>();
        List<MeshIsland> islands = new List<MeshIsland>();
        int triangleCount = triangles.Length / 3;

        for (int t = 0; t < triangleCount; t++)
        {
            if (visited.Contains(t)) continue;

            MeshIsland island = new MeshIsland();
            Queue<int> queue = new Queue<int>();
            queue.Enqueue(t);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                
                if (visited.Contains(current)) continue;
                visited.Add(current);
                
                island.TriangleIndices.Add(current);

                foreach (int adjacent in triangleAdjacency[current])
                {
                    if (!visited.Contains(adjacent))
                    {
                        queue.Enqueue(adjacent);
                    }
                }
            }

            // Calculate island properties
            CalculateIslandProperties(island, vertices, triangles);
            islands.Add(island);
        }

        return islands;
    }

    private static void CalculateIslandProperties(MeshIsland island, Vector3[] vertices, int[] triangles)
    {
        if (island.TriangleIndices.Count == 0) return;

        Vector3 min = Vector3.positiveInfinity;
        Vector3 max = Vector3.negativeInfinity;
        Vector3 centroidSum = Vector3.zero;
        float totalArea = 0f;
        float volume = 0f;

        foreach (int triIndex in island.TriangleIndices)
        {
            int t = triIndex * 3;
            Vector3 v0 = vertices[triangles[t]];
            Vector3 v1 = vertices[triangles[t + 1]];
            Vector3 v2 = vertices[triangles[t + 2]];

            // Update bounds
            min = Vector3.Min(min, Vector3.Min(v0, Vector3.Min(v1, v2)));
            max = Vector3.Max(max, Vector3.Max(v0, Vector3.Max(v1, v2)));

            // Calculate triangle area and centroid contribution
            Vector3 cross = Vector3.Cross(v1 - v0, v2 - v0);
            float area = cross.magnitude * 0.5f;
            Vector3 triCentroid = (v0 + v1 + v2) / 3f;
            
            centroidSum += triCentroid * area;
            totalArea += area;

            // Signed volume contribution (for closed meshes)
            volume += Vector3.Dot(v0, cross) / 6f;
        }

        island.Bounds = new Bounds((min + max) * 0.5f, max - min);
        island.Centroid = totalArea > 0 ? centroidSum / totalArea : (min + max) * 0.5f;
        island.Volume = Mathf.Abs(volume);
    }

    /// <summary>
    /// Separate islands into individual meshes.
    /// </summary>
    public static Mesh[] SeparateIslands(Mesh originalMesh, List<MeshIsland> islands)
    {
        if (islands == null || islands.Count <= 1)
        {
            return new Mesh[] { originalMesh };
        }

        Vector3[] originalVertices = originalMesh.vertices;
        Vector3[] originalNormals = originalMesh.normals;
        Vector2[] originalUVs = originalMesh.uv;
        int[] originalTriangles = originalMesh.triangles;

        Mesh[] separatedMeshes = new Mesh[islands.Count];

        for (int i = 0; i < islands.Count; i++)
        {
            MeshIsland island = islands[i];
            
            // Collect unique vertices for this island
            Dictionary<int, int> vertexRemap = new Dictionary<int, int>();
            List<Vector3> newVertices = new List<Vector3>();
            List<Vector3> newNormals = new List<Vector3>();
            List<Vector2> newUVs = new List<Vector2>();
            List<int> newTriangles = new List<int>();

            foreach (int triIndex in island.TriangleIndices)
            {
                int t = triIndex * 3;
                
                for (int j = 0; j < 3; j++)
                {
                    int originalIndex = originalTriangles[t + j];
                    
                    if (!vertexRemap.ContainsKey(originalIndex))
                    {
                        vertexRemap[originalIndex] = newVertices.Count;
                        newVertices.Add(originalVertices[originalIndex]);
                        
                        if (originalNormals != null && originalNormals.Length > originalIndex)
                        {
                            newNormals.Add(originalNormals[originalIndex]);
                        }
                        
                        if (originalUVs != null && originalUVs.Length > originalIndex)
                        {
                            newUVs.Add(originalUVs[originalIndex]);
                        }
                    }
                    
                    newTriangles.Add(vertexRemap[originalIndex]);
                }
            }

            // Create new mesh
            Mesh newMesh = new Mesh();
            newMesh.name = originalMesh.name + "_Island" + i;
            newMesh.indexFormat = newVertices.Count > 65535 
                ? UnityEngine.Rendering.IndexFormat.UInt32 
                : UnityEngine.Rendering.IndexFormat.UInt16;
            
            newMesh.vertices = newVertices.ToArray();
            newMesh.triangles = newTriangles.ToArray();
            
            if (newNormals.Count > 0)
            {
                newMesh.normals = newNormals.ToArray();
            }
            else
            {
                newMesh.RecalculateNormals();
            }
            
            if (newUVs.Count > 0)
            {
                newMesh.uv = newUVs.ToArray();
            }
            
            newMesh.RecalculateBounds();
            
            separatedMeshes[i] = newMesh;
        }

        return separatedMeshes;
    }

    /// <summary>
    /// Create GameObjects for each island.
    /// </summary>
    public static GameObject[] CreateIslandGameObjects(Mesh originalMesh, 
        List<MeshIsland> islands, Material material, Transform parent = null)
    {
        Mesh[] meshes = SeparateIslands(originalMesh, islands);
        GameObject[] gameObjects = new GameObject[meshes.Length];

        for (int i = 0; i < meshes.Length; i++)
        {
            GameObject go = new GameObject(meshes[i].name);
            
            if (parent != null)
            {
                go.transform.SetParent(parent);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
            }

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = meshes[i];

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;

            // Optionally add physics
            MeshCollider mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = meshes[i];
            mc.convex = false;

            // Add rigidbody for separated pieces (optional)
            if (i > 0) // Keep first island static
            {
                Rigidbody rb = go.AddComponent<Rigidbody>();
                rb.mass = islands[i].Volume * 100f; // Approximate mass from volume
            }

            gameObjects[i] = go;
        }

        return gameObjects;
    }

    /// <summary>
    /// Filter out small islands (debris) below a volume threshold.
    /// </summary>
    public static List<MeshIsland> FilterSmallIslands(List<MeshIsland> islands, float minVolume)
    {
        return islands.FindAll(island => island.Volume >= minVolume);
    }

    /// <summary>
    /// Get the largest island (usually the main mesh body).
    /// </summary>
    public static MeshIsland GetLargestIsland(List<MeshIsland> islands)
    {
        if (islands == null || islands.Count == 0) return null;

        MeshIsland largest = islands[0];
        foreach (var island in islands)
        {
            if (island.Volume > largest.Volume)
            {
                largest = island;
            }
        }
        return largest;
    }
}