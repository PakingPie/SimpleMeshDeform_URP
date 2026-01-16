using UnityEngine;

/// <summary>
/// Represents a cylindrical drill tool that can carve into grindable meshes.
/// Vertices inside the cylinder are pushed along the drill axis to create a hole.
/// </summary>
public class DrillTool : MonoBehaviour
{
    [Header("Drill Geometry")]
    [Tooltip("Radius of the drill cylinder")]
    [SerializeField] private float radius = 0.1f;

    [Tooltip("Length of the drill cylinder")]
    [SerializeField] private float length = 1f;

    [Header("Axis Configuration")]
    [Tooltip("Local axis that points in the drilling direction (tip direction)")]
    [SerializeField] private DrillAxisDirection drillAxis = DrillAxisDirection.NegativeY;

    [Header("Depth Control")]
    [Tooltip("If true, uses configurable depth instead of full cylinder length")]
    [SerializeField] private bool useConfigurableDepth = false;

    [Tooltip("Custom drilling depth (only used if useConfigurableDepth is true)")]
    [SerializeField] private float configurableDepth = 0.5f;

    [Header("Drilling Mode")]
    [Tooltip("How vertices are displaced when inside the drill cylinder")]
    [SerializeField] private DrillMode drillMode = DrillMode.PushAlongAxis;

    [Header("Progressive Drilling")]
    [Tooltip("How fast vertices are pushed per frame")]
    [SerializeField] private float pushSpeed = 0.01f;

    [Tooltip("Additional radial push to smooth hole edges (0 = no radial push)")]
    [SerializeField] private float radialSmoothingSpeed = 0.002f;

    [Header("Detection")]
    [Tooltip("Margin inside the radius to detect vertices")]
    [SerializeField] private float surfaceMargin = 0.001f;

    [Header("Debug Visualization")]
    [SerializeField] private bool showDebugCylinder = true;
    [SerializeField] private Color cylinderColor = new Color(0f, 1f, 0f, 0.5f);
    [SerializeField] private int debugSegments = 32;

    public enum DrillAxisDirection
    {
        PositiveX,
        NegativeX,
        PositiveY,
        NegativeY,
        PositiveZ,
        NegativeZ
    }

    public enum DrillMode
    {
        /// <summary>
        /// Push vertices along the drill axis (opposite to drill direction).
        /// Creates a proper hole by moving material "out" of the drill path.
        /// </summary>
        PushAlongAxis,
        
        /// <summary>
        /// Push vertices radially outward to the cylinder surface.
        /// Only works for initial contact, not for deepening holes.
        /// </summary>
        PushRadially,
        
        /// <summary>
        /// Instantly project vertices to the nearest exit point.
        /// </summary>
        InstantProject
    }

    // Properties
    public float Radius => radius;
    public float EffectiveLength => useConfigurableDepth ? configurableDepth : length;
    public float Length => length;
    public DrillMode Mode => drillMode;
    public float PushSpeed => pushSpeed;
    public float RadialSmoothingSpeed => radialSmoothingSpeed;
    public float SurfaceMargin => surfaceMargin;
    
    // Legacy properties for compatibility
    public bool UseProgressiveDrilling => drillMode != DrillMode.InstantProject;
    public float RadialPushSpeed => radialSmoothingSpeed;
    public float AxialPushSpeed => pushSpeed;

    public Vector3 DrillDirection
    {
        get
        {
            switch (drillAxis)
            {
                case DrillAxisDirection.PositiveX: return transform.right;
                case DrillAxisDirection.NegativeX: return -transform.right;
                case DrillAxisDirection.PositiveY: return transform.up;
                case DrillAxisDirection.NegativeY: return -transform.up;
                case DrillAxisDirection.PositiveZ: return transform.forward;
                case DrillAxisDirection.NegativeZ: return -transform.forward;
                default: return -transform.up;
            }
        }
    }

    /// <summary>
    /// Direction material should be pushed (opposite to drill direction).
    /// </summary>
    public Vector3 PushDirection => -DrillDirection;

    public Vector3 DrillBase => transform.position;
    public Vector3 DrillTip => transform.position + DrillDirection * EffectiveLength;

    public (int axisIndex, float sign) GetLocalAxisInfo()
    {
        switch (drillAxis)
        {
            case DrillAxisDirection.PositiveX: return (0, 1f);
            case DrillAxisDirection.NegativeX: return (0, -1f);
            case DrillAxisDirection.PositiveY: return (1, 1f);
            case DrillAxisDirection.NegativeY: return (1, -1f);
            case DrillAxisDirection.PositiveZ: return (2, 1f);
            case DrillAxisDirection.NegativeZ: return (2, -1f);
            default: return (1, -1f);
        }
    }

    /// <summary>
    /// Checks if a world-space point is inside the drill cylinder.
    /// </summary>
    public bool IsPointInsideCylinder(Vector3 worldPoint)
    {
        Vector3 basePos = DrillBase;
        Vector3 direction = DrillDirection;
        
        Vector3 toPoint = worldPoint - basePos;
        float height = Vector3.Dot(toPoint, direction);
        
        if (height < 0 || height > EffectiveLength)
            return false;
        
        Vector3 axialComponent = direction * height;
        Vector3 radialVector = toPoint - axialComponent;
        float radialDistSqr = radialVector.sqrMagnitude;
        
        float effectiveRadius = radius - surfaceMargin;
        return radialDistSqr < effectiveRadius * effectiveRadius;
    }

    /// <summary>
    /// Projects/pushes a point inside the cylinder based on the current drill mode.
    /// </summary>
    /// <param name="worldPoint">The world-space point to process</param>
    /// <param name="wasProjected">True if the point was inside and got modified</param>
    public Vector3 ProjectToSurface(Vector3 worldPoint, out bool wasProjected)
    {
        Vector3 basePos = DrillBase;
        Vector3 direction = DrillDirection;
        
        // Vector from drill base to the point
        Vector3 toPoint = worldPoint - basePos;
        
        // Project onto drill axis to get height along cylinder
        float height = Vector3.Dot(toPoint, direction);
        
        // Check height bounds (must be between base and tip)
        if (height < 0 || height > EffectiveLength)
        {
            wasProjected = false;
            return worldPoint;
        }
        
        // Get the radial vector (perpendicular to drill axis)
        Vector3 axialComponent = direction * height;
        Vector3 radialVector = toPoint - axialComponent;
        float radialDistSqr = radialVector.sqrMagnitude;
        
        // Detection radius
        float detectionRadius = radius - surfaceMargin;
        float detectionRadiusSqr = detectionRadius * detectionRadius;
        
        if (radialDistSqr >= detectionRadiusSqr)
        {
            wasProjected = false;
            return worldPoint;
        }
        
        // Point is inside cylinder
        wasProjected = true;
        
        float radialDist = Mathf.Sqrt(radialDistSqr);
        
        switch (drillMode)
        {
            case DrillMode.PushAlongAxis:
                return CalculateAxialPush(worldPoint, basePos, direction, radialVector, radialDist, height);
            
            case DrillMode.PushRadially:
                return CalculateRadialPush(worldPoint, basePos, direction, axialComponent, radialVector, radialDist, height);
            
            case DrillMode.InstantProject:
                return CalculateInstantProjection(worldPoint, basePos, direction, axialComponent, radialVector, radialDist, height);
            
            default:
                return worldPoint;
        }
    }

    /// <summary>
    /// Pushes vertex along the drill axis (out of the hole).
    /// This is the main drilling method that creates proper holes.
    /// </summary>
    private Vector3 CalculateAxialPush(Vector3 worldPoint, Vector3 basePos, Vector3 direction,
        Vector3 radialVector, float radialDist, float height)
    {
        Vector3 result = worldPoint;
        
        // Main push: along the opposite direction of drilling (push material OUT)
        // Vertices closer to the tip get pushed more (they're "freshly contacted")
        float normalizedHeight = height / EffectiveLength;
        
        // Push strength increases toward the tip
        // At tip (height = EffectiveLength, normalizedHeight = 1): full push
        // At base (height = 0, normalizedHeight = 0): minimal push
        float axialPushStrength = pushSpeed * (0.5f + normalizedHeight * 0.5f);
        
        // Push in the direction opposite to drilling (toward the base/exit)
        result += PushDirection * axialPushStrength;
        
        // Optional: slight radial smoothing for vertices near the edge
        // This helps create smoother hole walls
        if (radialSmoothingSpeed > 0 && radialDist > 0.0001f)
        {
            // Only apply radial smoothing to vertices near the cylinder edge
            float edgeProximity = radialDist / radius; // 0 at center, ~1 at edge
            
            if (edgeProximity > 0.7f)
            {
                Vector3 radialDirection = radialVector / radialDist;
                float radialPush = radialSmoothingSpeed * (edgeProximity - 0.7f) / 0.3f;
                result += radialDirection * radialPush;
            }
        }
        
        return result;
    }

    /// <summary>
    /// Pushes vertex radially outward (old method, kept for compatibility).
    /// </summary>
    private Vector3 CalculateRadialPush(Vector3 worldPoint, Vector3 basePos, Vector3 direction,
        Vector3 axialComponent, Vector3 radialVector, float radialDist, float height)
    {
        Vector3 result = worldPoint;
        
        float penetrationDepth = radius - radialDist;
        float normalizedPenetration = penetrationDepth / radius;
        
        if (radialDist < 0.0001f)
        {
            Vector3 perpendicular = GetArbitraryPerpendicular(direction);
            result += perpendicular * pushSpeed;
        }
        else
        {
            Vector3 radialDirection = radialVector / radialDist;
            float radialPush = pushSpeed * (1f + normalizedPenetration);
            result += radialDirection * radialPush;
        }
        
        return result;
    }

    /// <summary>
    /// Instantly projects vertex to the nearest exit point.
    /// For drilling, this means projecting to the base plane.
    /// </summary>
    private Vector3 CalculateInstantProjection(Vector3 worldPoint, Vector3 basePos, Vector3 direction,
        Vector3 axialComponent, Vector3 radialVector, float radialDist, float height)
    {
        // Project to the base plane (exit of the drill hole)
        // The vertex stays at its radial position but moves to height = 0
        Vector3 basePoint = basePos + radialVector;
        return basePoint;
    }

    private Vector3 GetArbitraryPerpendicular(Vector3 direction)
    {
        Vector3 perpendicular = Vector3.Cross(direction, Vector3.up);
        if (perpendicular.sqrMagnitude < 0.001f)
        {
            perpendicular = Vector3.Cross(direction, Vector3.right);
        }
        return perpendicular.normalized;
    }

    public Vector3 ProjectToSurfaceClamped(Vector3 worldPoint, Bounds originalBounds, out bool wasProjected)
    {
        Vector3 projected = ProjectToSurface(worldPoint, out wasProjected);

        if (wasProjected)
        {
            projected.x = Mathf.Clamp(projected.x, originalBounds.min.x, originalBounds.max.x);
            projected.y = Mathf.Clamp(projected.y, originalBounds.min.y, originalBounds.max.y);
            projected.z = Mathf.Clamp(projected.z, originalBounds.min.z, originalBounds.max.z);
        }

        return projected;
    }

    private void OnValidate()
    {
        radius = Mathf.Max(0.001f, radius);
        length = Mathf.Max(0.001f, length);
        configurableDepth = Mathf.Clamp(configurableDepth, 0.001f, length);
        pushSpeed = Mathf.Max(0.0001f, pushSpeed);
        radialSmoothingSpeed = Mathf.Max(0f, radialSmoothingSpeed);
        surfaceMargin = Mathf.Clamp(surfaceMargin, 0.0001f, radius * 0.1f);
    }

    private void OnDrawGizmos()
    {
        if (!showDebugCylinder) return;
        DrawCylinderGizmo();
    }

    private void OnDrawGizmosSelected()
    {
        DrawCylinderGizmo();
    }

    private void DrawCylinderGizmo()
    {
        Gizmos.color = cylinderColor;

        Vector3 baseCenter = DrillBase;
        Vector3 tipCenter = DrillTip;
        Vector3 direction = DrillDirection;

        Vector3 perpendicular1 = Vector3.Cross(direction, Vector3.up).normalized;
        if (perpendicular1.sqrMagnitude < 0.001f)
        {
            perpendicular1 = Vector3.Cross(direction, Vector3.right).normalized;
        }
        Vector3 perpendicular2 = Vector3.Cross(direction, perpendicular1).normalized;

        DrawCircle(baseCenter, perpendicular1, perpendicular2, radius);
        DrawCircle(tipCenter, perpendicular1, perpendicular2, radius);

        for (int i = 0; i < 8; i++)
        {
            float angle = i * Mathf.PI * 2f / 8f;
            Vector3 offset = (perpendicular1 * Mathf.Cos(angle) + perpendicular2 * Mathf.Sin(angle)) * radius;
            Gizmos.DrawLine(baseCenter + offset, tipCenter + offset);
        }

        // Draw drill direction arrow (yellow)
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(tipCenter, tipCenter + direction * 0.1f);
        
        // Draw push direction arrow (cyan) - direction material moves
        Gizmos.color = Color.cyan;
        Vector3 pushDir = PushDirection;
        Gizmos.DrawLine(baseCenter, baseCenter + pushDir * 0.15f);
        
        // Draw detection radius
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.3f);
        float detectionRadius = radius - surfaceMargin;
        DrawCircle(baseCenter, perpendicular1, perpendicular2, detectionRadius);
        DrawCircle(tipCenter, perpendicular1, perpendicular2, detectionRadius);
    }

    private void DrawCircle(Vector3 center, Vector3 axis1, Vector3 axis2, float circleRadius)
    {
        Vector3 prevPoint = center + axis1 * circleRadius;
        for (int i = 1; i <= debugSegments; i++)
        {
            float angle = i * Mathf.PI * 2f / debugSegments;
            Vector3 point = center + (axis1 * Mathf.Cos(angle) + axis2 * Mathf.Sin(angle)) * circleRadius;
            Gizmos.DrawLine(prevPoint, point);
            prevPoint = point;
        }
    }
}