using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class ObjectSpawner : MonoBehaviour
{
    public GameObject[] prefabs;
    public float spawnInterval = 2f;
    public float spawnRangeX = 5f;
    public float spawnHeight = 10f;

    void Start()
    {
        StartCoroutine(SpawnRoutine());
    }

    IEnumerator SpawnRoutine()
    {
        while (true)
        {
            if (prefabs != null && prefabs.Length > 0)
            {
                GameObject prefab = prefabs[Random.Range(0, prefabs.Length)];
                Vector3 spawnPos = new Vector3(Random.Range(-spawnRangeX, spawnRangeX), spawnHeight, 0);
                GameObject go = Instantiate(prefab, spawnPos, Random.rotation);
                
                // Ensure it has Rigidbody, MeshCollider and Sliceable
                SetupObject(go);
            }
            yield return new WaitForSeconds(spawnInterval);
        }
    }

    void SetupObject(GameObject go)
    {
        var rb = go.GetComponent<Rigidbody>();
        if (rb == null) rb = go.AddComponent<Rigidbody>();

        var mf = go.GetComponentInChildren<MeshFilter>();
        if (mf != null)
        {
            // Add collider to the same object as the MeshFilter for correct alignment
            var mc = mf.gameObject.GetComponent<MeshCollider>();
            if (mc == null) mc = mf.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.sharedMesh;
            mc.convex = true;
        }

        var sliceable = go.GetComponent<Sliceable>();
        if (sliceable == null) sliceable = go.AddComponent<Sliceable>();
        
        var mr = go.GetComponentInChildren<MeshRenderer>();
        if (mr != null) sliceable.material = mr.sharedMaterial;
    }
}
