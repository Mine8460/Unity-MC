using System;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerInteractions : MonoBehaviour
{
    [SerializeField] private Transform camera;
    [SerializeField] private float interactionRange = 5f;
    [SerializeField] private BlockType blockToPlace = BlockType.Stone;

    void Start()
    {

    }

    void Update()
    {

    }

    public void OnBreak(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            BlockType type = GetBlockTypeInsideCamera();
            if (type == BlockType.Air)
            {
                RaycastHit hit;
                if (Physics.Raycast(camera.position, camera.forward, out hit, interactionRange))
                {
                    Vector3 point = hit.point - hit.normal * 0.02f; // Move the point slightly inside the block
                    GetComponent<PlayerController>().world.BreakBlock(Mathf.FloorToInt(point.x), Mathf.FloorToInt(point.y), Mathf.FloorToInt(point.z));
                }
            }
            else
            {
                Vector3 point = camera.position;
                GetComponent<PlayerController>().world.BreakBlock(Mathf.FloorToInt(point.x), Mathf.FloorToInt(point.y), Mathf.FloorToInt(point.z));
            }
        }
    }
    public void OnPlace(InputAction.CallbackContext context)
    {
        if (context.performed && blockToPlace != BlockType.Bedrock)
        {
            RaycastHit hit;
            if (Physics.Raycast(camera.position, camera.forward, out hit, interactionRange))
            {
                Vector3 point = hit.point + hit.normal * 0.02f; // Move the point slightly inside the block
                GetComponent<PlayerController>().world.PlaceBlock(Mathf.FloorToInt(point.x), Mathf.FloorToInt(point.y), Mathf.FloorToInt(point.z), blockToPlace);

            }
        }
    }

    public void OnCopyBlock(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            BlockType type = GetBlockTypeInsideCamera();
            if (type == BlockType.Air)
            {
                RaycastHit hit;
                if (Physics.Raycast(camera.position, camera.forward, out hit, interactionRange))
                {
                    Vector3 point = hit.point - hit.normal * 0.02f; // Move the point slightly inside the block
                    blockToPlace = GetComponent<PlayerController>().world.GetBlock(Mathf.FloorToInt(point.x), Mathf.FloorToInt(point.y), Mathf.FloorToInt(point.z));
                }
            }
            else
            {
                Vector3 point = camera.position;
                blockToPlace = GetComponent<PlayerController>().world.GetBlock(Mathf.FloorToInt(point.x), Mathf.FloorToInt(point.y), Mathf.FloorToInt(point.z));
            }
        }
    }

    BlockType GetBlockTypeInsideCamera()
    {
        Vector3 pos = camera.position;
        return GetComponent<PlayerController>().world.GetBlock(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y), Mathf.FloorToInt(pos.z));
    }
}
