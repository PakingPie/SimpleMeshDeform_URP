// using UnityEngine;
// using System;

// public class AttachmentZone : MonoBehaviour
// {
//     [Header("Object to Attach")]
//     public Transform attachableObject;
    
//     [Header("Controller Settings")]
//     public OVRInput.Controller targetController = OVRInput.Controller.RTouch;

//     [Header("Attachment Settings")]
//     public Vector3 attachOffset = Vector3.zero;
//     public Vector3 attachRotationOffset = Vector3.zero;

//     [Header("Detection")]
//     public string playerTag = "Player";

//     [Header("References")]
//     [Tooltip("The safe zone that handles re-attachment after initial pickup")]
//     public CubeWireframe safeZone;

//     [Header("Detach Behavior")]
//     [Tooltip("If true, object stays where it was detached. If false, returns to origin.")]
//     public bool detachInPlace = true;

//     // Events
//     public event Action OnObjectAttached;
//     public event Action OnObjectDetached;

//     private Transform controllerTransform;
//     private Transform originalParent;
//     private Vector3 originalPosition;
//     private Quaternion originalRotation;
//     private bool isAttached = false;
//     private Rigidbody objectRb;

//     // Once picked up initially, this zone is disabled
//     private bool initialPickupComplete = false;

//     public bool IsAttached => isAttached;
//     public Transform ControllerTransform => controllerTransform;

//     private void Start()
//     {
//         if (attachableObject == null)
//         {
//             Debug.LogError("AttachmentZone: No attachable object assigned!");
//             enabled = false;
//             return;
//         }

//         // Store original transform
//         originalParent = attachableObject.parent;
//         originalPosition = attachableObject.position;
//         originalRotation = attachableObject.rotation;

//         objectRb = attachableObject.GetComponent<Rigidbody>();

//         // Find controller (Unity 6 compatible)
//         OVRCameraRig cameraRig = FindFirstObjectByType<OVRCameraRig>();
//         if (cameraRig != null)
//         {
//             controllerTransform = targetController == OVRInput.Controller.RTouch
//                 ? cameraRig.rightHandAnchor
//                 : cameraRig.leftHandAnchor;
//         }
//         else
//         {
//             Debug.LogWarning("AttachmentZone: OVRCameraRig not found!");
//         }

//         // Ensure this has a trigger collider
//         Collider col = GetComponent<Collider>();
//         if (col != null)
//         {
//             col.isTrigger = true;
//         }

//         // Initialize safe zone reference
//         if (safeZone != null)
//         {
//             safeZone.Initialize(this);
//         }
//     }

//     private void OnTriggerEnter(Collider other)
//     {
//         TryInitialAttach(other);
//     }

//     private void OnTriggerStay(Collider other)
//     {
//         TryInitialAttach(other);
//     }

//     private void OnTriggerExit(Collider other)
//     {
//         if (isAttached && IsPlayer(other))
//         {
//             Detach();
//         }
//     }

//     private void TryInitialAttach(Collider other)
//     {
//         // Only handle initial pickup - after that, CubeWireframe takes over
//         if (initialPickupComplete) return;
//         if (isAttached) return;
//         if (!IsPlayer(other)) return;

//         Attach();
//         initialPickupComplete = true;

//         Debug.Log("[AttachmentZone] Initial pickup complete. SafeZone now handles re-attachment.");
//     }

//     private bool IsPlayer(Collider other)
//     {
//         if (other == null) return false;
//         if (other.CompareTag(playerTag)) return true;

//         OVRCameraRig rig = other.GetComponentInParent<OVRCameraRig>();
//         return rig != null;
//     }

//     public void Attach()
//     {
//         if (controllerTransform == null || attachableObject == null) return;
//         if (isAttached) return;

//         if (objectRb != null)
//         {
//             objectRb.isKinematic = true;
//             objectRb.useGravity = false;
//         }

//         attachableObject.SetParent(controllerTransform);
//         attachableObject.localPosition = attachOffset;
//         attachableObject.localRotation = Quaternion.Euler(attachRotationOffset);

//         isAttached = true;

//         OnObjectAttached?.Invoke();
//         Debug.Log($"[AttachmentZone] Object attached to {targetController}");
//     }

//     public void Detach()
//     {
//         if (attachableObject == null) return;
//         if (!isAttached) return;

//         // Store current world position/rotation BEFORE unparenting
//         Vector3 currentPosition = attachableObject.position;
//         Quaternion currentRotation = attachableObject.rotation;

//         // Unparent
//         attachableObject.SetParent(originalParent);

//         if (detachInPlace)
//         {
//             // Stay at breach position
//             attachableObject.position = currentPosition;
//             attachableObject.rotation = currentRotation;
//         }
//         else
//         {
//             // Return to original position
//             attachableObject.position = originalPosition;
//             attachableObject.rotation = originalRotation;
//         }

//         if (objectRb != null)
//         {
//             objectRb.isKinematic = false;
//             objectRb.useGravity = true;
//         }

//         isAttached = false;

//         OnObjectDetached?.Invoke();
//         Debug.Log($"[AttachmentZone] Object detached {(detachInPlace ? "in place" : "at origin")}");
//     }

//     /// <summary>
//     /// Full reset - allows AttachmentZone to be used again for initial pickup
//     /// </summary>
//     public void FullReset()
//     {
//         Detach();
//         initialPickupComplete = false;
//         Debug.Log("[AttachmentZone] Full reset - initial pickup zone reactivated");
//     }

//     private void OnDrawGizmos()
//     {
//         // Gray out if initial pickup is complete
//         Gizmos.color = initialPickupComplete
//             ? new Color(0.5f, 0.5f, 0.5f, 0.3f)
//             : (isAttached ? Color.green : Color.yellow);
//         Gizmos.DrawWireCube(transform.position, transform.localScale);
//     }
// }