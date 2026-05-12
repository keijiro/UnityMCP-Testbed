using UnityEngine;

namespace TriangleSplatting
{
    [RequireComponent(typeof(Camera))]
    public sealed class FlyCamera : MonoBehaviour
    {
        public float moveSpeed = 5.0f;
        public float boostMultiplier = 5.0f;
        public float lookSensitivity = 2.0f;
        public float scrollSpeedMultiplier = 1.25f;

        Vector2 _euler;

        void OnEnable()
        {
            var e = transform.eulerAngles;
            _euler = new Vector2(e.y, e.x);
        }

        void Update()
        {
            if (Input.GetMouseButton(1))
            {
                _euler.x += Input.GetAxis("Mouse X") * lookSensitivity;
                _euler.y -= Input.GetAxis("Mouse Y") * lookSensitivity;
                _euler.y = Mathf.Clamp(_euler.y, -89f, 89f);
                transform.rotation = Quaternion.Euler(_euler.y, _euler.x, 0);
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            var input = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) input.z += 1;
            if (Input.GetKey(KeyCode.S)) input.z -= 1;
            if (Input.GetKey(KeyCode.D)) input.x += 1;
            if (Input.GetKey(KeyCode.A)) input.x -= 1;
            if (Input.GetKey(KeyCode.E)) input.y += 1;
            if (Input.GetKey(KeyCode.Q)) input.y -= 1;

            var scroll = Input.mouseScrollDelta.y;
            if (scroll != 0)
                moveSpeed *= Mathf.Pow(scrollSpeedMultiplier, Mathf.Sign(scroll));

            var speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? boostMultiplier : 1f);
            transform.position += transform.TransformDirection(input) * (speed * Time.deltaTime);
        }
    }
}
