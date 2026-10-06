using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.LightTransport;
using UnityEngine.UI;

public class PlayerInteractions : MonoBehaviour
{
    [SerializeField] private Transform camera;
    [SerializeField] private BlockOutline outline;
    [SerializeField] private float interactionRange = 5f;
    [SerializeField] private BlockType blockToPlace = BlockType.Stone;

    void Start()
    {

    }

    void Update()
    {
        if (Physics.Raycast(camera.position, camera.forward, out RaycastHit hit, interactionRange))
            outline.ShowFromHit(hit.point, hit.normal);
        else
            outline.Hide();
    }
    public void OnPlace(InputAction.CallbackContext context)
    {
        if (context.performed && blockToPlace != BlockType.Bedrock && !Inventory.IsOpen)
        {
            Inventory inventory = GetComponent<Inventory>();
            World world = GetComponent<PlayerController>().world;
            RaycastHit hit;
            if (Physics.Raycast(camera.position, camera.forward, out hit, interactionRange))
            {
                Vector3 point = hit.point + hit.normal * 0.02f; // Move the point slightly inside the block
                BlockType type = GetComponent<PlayerController>().world.GetBlock(Mathf.FloorToInt(point.x), Mathf.FloorToInt(point.y), Mathf.FloorToInt(point.z));

                Vector3Int hitBlock = Vector3Int.FloorToInt(hit.point - hit.normal * 0.01f);
                Vector3Int normal = Vector3Int.RoundToInt(hit.normal);
                if (inventory.TryGetSelected(out BlockType selected) &&
                    world.TryPlaceAgainst(hitBlock, normal, hit.point, selected))
                    inventory.ConsumeSelected();
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
