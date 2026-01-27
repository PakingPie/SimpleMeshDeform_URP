// using UnityEngine;
// using System;

// [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
// public class CubeWireframe : MonoBehaviour
// {
//     [Header("Wireframe Appearance")]
//     public Color defaultColor = Color.white;
//     public Color activeColor = Color.green;
//     public Color breachColor = Color.red;
    
//     [Header("Material")]
//     public Material wireMaterial;
    
//     [Header("Detection")]
//     public string playerTag = "Player";
    
//     [Header("Re-attachment Settings")]
//     [Tooltip("Delay before player can re-attach after breach")]
//     public float reattachCooldown = 0.5f;

//     // Events
//     public event Action OnBreach;
//     public event Action OnReattach;

//     private GameObject wireframeObject;
//     private Material instanceMaterial;
//     private AttachmentZone attachmentZone;
    
//     private bool isPlayerInside = false;
//     private bool canReattach = true;
//     private float lastBreachTime = -Mathf.Infinity;

//     void Start()
//     {
//         CreateWireframe();
//         SetWireColor(defaultColor);
        
//         // Ensure we have a trigger collider
//         Collider col = GetComponent<Collider>();
//         if (col == null)
//         {
//             BoxCollider box = gameObject.AddComponent<BoxCollider>();
//             box.isTrigger = true;
//         }
//         else
//         {
//             col.isTrigger = true;
//         }
//     }
    
//     /// <summary>
//     /// Initialize with reference to the attachment zone
//     /// Called by AttachmentZone.Start()
//     /// </summary>
//     public void Initialize(AttachmentZone zone)
//     {
//         attachmentZone = zone;
//     }

//     /// <summary>
//     /// Check if a point is inside the wireframe bounds
//     /// </summary>
//     public bool IsInsideWireframe(Vector3 point)
//     {
//         Bounds bounds = new Bounds(transform.position, transform.localScale);
//         return bounds.Contains(point);
//     }

//     void OnTriggerEnter(Collider other)
//     {
//         if (!IsPlayer(other)) return;
        
//         isPlayerInside = true;
        
//         // Try to re-attach if object is currently detached
//         TryReattach();
//     }
    
//     void OnTriggerStay(Collider other)
//     {
//         if (!IsPlayer(other)) return;
        
//         isPlayerInside = true;
        
//         // Continuously try to re-attach while inside
//         TryReattach();
//     }

//     void OnTriggerExit(Collider other)
//     {
//         if (!IsPlayer(other)) return;
        
//         isPlayerInside = false;
        
//         // Breach! Player left the safe zone while holding object
//         if (attachmentZone != null && attachmentZone.IsAttached)
//         {
//             HandleBreach();
//         }
//     }
    
//     private void TryReattach()
//     {
//         if (attachmentZone == null) return;
        
//         // Already attached, nothing to do
//         if (attachmentZone.IsAttached) return;
        
//         // Check cooldown
//         if (!canReattach)
//         {
//             if (UnityEngine.Time.time - lastBreachTime >= reattachCooldown)
//             {
//                 canReattach = true;
//             }
//             else
//             {
//                 return;
//             }
//         }
        
//         // Re-attach!
//         attachmentZone.Attach();
//         SetWireColor(activeColor);
        
//         OnReattach?.Invoke();
//         Debug.Log("[CubeWireframe] Object re-attached within safe zone");
//     }
    
//     private void HandleBreach()
//     {
//         // Visual feedback
//         SetWireColor(breachColor);
        
//         // Detach the object
//         attachmentZone.Detach();
        
//         // Start cooldown
//         canReattach = false;
//         lastBreachTime = UnityEngine.Time.time;
        
//         // Reset color after a moment
//         Invoke(nameof(ResetToDefaultColor), 0.3f);
        
//         OnBreach?.Invoke();
//         Debug.Log("[CubeWireframe] Breach! Object detached. Player must re-enter to reattach.");
//     }
    
//     private void ResetToDefaultColor()
//     {
//         if (attachmentZone != null && attachmentZone.IsAttached)
//         {
//             SetWireColor(activeColor);
//         }
//         else
//         {
//             SetWireColor(defaultColor);
//         }
//     }
    
//     private bool IsPlayer(Collider other)
//     {
//         if (other == null) return false;
//         if (other.CompareTag(playerTag)) return true;
        
//         OVRCameraRig rig = other.GetComponentInParent<OVRCameraRig>();
//         return rig != null;
//     }
    
//     private void SetWireColor(Color color)
//     {
//         if (instanceMaterial != null)
//         {
//             instanceMaterial.color = color;
//         }
//     }

//     void CreateWireframe()
//     {
//         wireframeObject = new GameObject("Wireframe");
//         wireframeObject.transform.SetParent(transform);
//         wireframeObject.transform.localPosition = Vector3.zero;
//         wireframeObject.transform.localRotation = Quaternion.identity;
//         wireframeObject.transform.localScale = Vector3.one;

//         MeshFilter mf = wireframeObject.AddComponent<MeshFilter>();
//         MeshRenderer mr = wireframeObject.AddComponent<MeshRenderer>();

//         Mesh wireMesh = new Mesh();
//         wireMesh.name = "CubeWireframe";

//         float h = 0.5f;
//         Vector3[] vertices = new Vector3[]
//         {
//             new Vector3(-h, -h, -h),
//             new Vector3( h, -h, -h),
//             new Vector3( h, -h,  h),
//             new Vector3(-h, -h,  h),
//             new Vector3(-h,  h, -h),
//             new Vector3( h,  h, -h),
//             new Vector3( h,  h,  h),
//             new Vector3(-h,  h,  h)
//         };

//         int[] indices = new int[]
//         {
//             0, 1, 1, 2, 2, 3, 3, 0,  // Bottom
//             4, 5, 5, 6, 6, 7, 7, 4,  // Top
//             0, 4, 1, 5, 2, 6, 3, 7   // Vertical
//         };

//         wireMesh.vertices = vertices;
//         wireMesh.SetIndices(indices, MeshTopology.Lines, 0);

//         mf.mesh = wireMesh;

//         if (wireMaterial == null)
//         {
//             wireMaterial = new Material(Shader.Find("Unlit/Color"));
//         }
//         instanceMaterial = new Material(wireMaterial);
//         instanceMaterial.color = defaultColor;
//         mr.material = instanceMaterial;
//     }

//     void OnDestroy()
//     {
//         if (wireframeObject != null) Destroy(wireframeObject);
//         if (instanceMaterial != null) Destroy(instanceMaterial);
//     }
    
//     private void OnDrawGizmos()
//     {
//         Gizmos.color = new Color(0f, 1f, 1f, 0.5f);
//         Gizmos.DrawWireCube(transform.position, transform.localScale);
//     }
// }