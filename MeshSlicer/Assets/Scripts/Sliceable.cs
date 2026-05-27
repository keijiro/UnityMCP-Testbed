using UnityEngine;
using MeshSlicer;
using Unity.Mathematics;
using Plane = Unity.Mathematics.Geometry.Plane;

public class Sliceable : MonoBehaviour
{
    public Material material;
    public float remainingLife = 10f;

    void Update()
    {
        remainingLife -= Time.deltaTime;
        if (remainingLife <= 0)
        {
            Destroy(gameObject);
        }
    }

    public void Slice(Plane worldPlane)
    {
        MeshFilter mf = GetComponentInChildren<MeshFilter>();
        if (mf == null) return;

        Mesh sourceMesh = mf.sharedMesh;
        if (sourceMesh == null) return;

        // Convert world plane to the local space of the MeshFilter's GameObject
        Transform meshTransform = mf.transform;
        float3 localNormal = meshTransform.InverseTransformDirection(worldPlane.Normal);
        float3 localPoint = meshTransform.InverseTransformPoint((float3)worldPlane.Normal * -worldPlane.Distance);
        var localPlane = Plane.CreateFromUnitNormalAndPointInPlane(localNormal, localPoint);

        var result = BurstMeshSlicer.Slice(sourceMesh, localPlane);

        if (result.Positive != null && result.Negative != null)
        {
            CreateHalf(result.Positive, "Positive", worldPlane.Normal, meshTransform);
            CreateHalf(result.Negative, "Negative", -worldPlane.Normal, meshTransform);
            Destroy(gameObject);
        }
    }

    private void CreateHalf(Mesh mesh, string name, float3 pushDir, Transform originalMeshTransform)
    {
        GameObject go = new GameObject(gameObject.name + "_" + name);
        // Position the new object at the world position of the original mesh node
        go.transform.position = originalMeshTransform.position;
        go.transform.rotation = originalMeshTransform.rotation;
        go.transform.localScale = originalMeshTransform.lossyScale;

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = material != null ? material : GetComponentInChildren<MeshRenderer>().sharedMaterial;

        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;
        mc.convex = true;

        var rb = go.AddComponent<Rigidbody>();
        var originalRb = GetComponent<Rigidbody>();
        if (originalRb != null)
        {
            rb.linearVelocity = originalRb.linearVelocity;
            rb.angularVelocity = originalRb.angularVelocity;
        }

        // Apply a small impulse and torque to separate the pieces with a twist
        rb.AddForce((Vector3)pushDir * 3.0f, ForceMode.Impulse);
        
        // Add random torque for "twist"
        Vector3 randomTorque = new Vector3(
            UnityEngine.Random.Range(-1f, 1f),
            UnityEngine.Random.Range(-1f, 1f),
            UnityEngine.Random.Range(-1f, 1f)
        ) * 5.0f;
        rb.AddTorque(randomTorque, ForceMode.Impulse);

        var newSliceable = go.AddComponent<Sliceable>();
        newSliceable.material = mr.sharedMaterial;
        // Inherit the remaining life time
        newSliceable.remainingLife = remainingLife;
    }
}
