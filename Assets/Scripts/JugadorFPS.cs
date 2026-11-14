using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class JugadorFPS : MonoBehaviour
{
    public Transform camara;
    public float velocidad = 4f;
    public float velocidadCorrer = 7f;
    public float salto = 1.2f;
    public float gravedad = -20f;
    public float sensibilidad = 0.1f;

    CharacterController cc;
    float velY;
    float rotX;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
    }

    void Start()
    {
        Bloquear(true);
    }

    void Bloquear(bool si)
    {
        Cursor.lockState = si ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !si;
    }

    void Update()
    {
        var k = Keyboard.current;
        var m = Mouse.current;
        if (k == null) return;

        if (k.escapeKey.wasPressedThisFrame) Bloquear(false);
        if (m != null && m.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked) Bloquear(true);

        // Mirar con el mouse
        if (m != null && Cursor.lockState == CursorLockMode.Locked && camara != null)
        {
            Vector2 d = m.delta.ReadValue() * sensibilidad;
            transform.Rotate(0f, d.x, 0f);
            rotX = Mathf.Clamp(rotX - d.y, -85f, 85f);
            camara.localEulerAngles = new Vector3(rotX, 0f, 0f);
        }

        // Moverse con WASD
        float x = (k.dKey.isPressed ? 1f : 0f) - (k.aKey.isPressed ? 1f : 0f);
        float z = (k.wKey.isPressed ? 1f : 0f) - (k.sKey.isPressed ? 1f : 0f);
        Vector3 dir = Vector3.ClampMagnitude(transform.right * x + transform.forward * z, 1f);
        float vel = k.leftShiftKey.isPressed ? velocidadCorrer : velocidad;

        // Gravedad y salto
        if (cc.isGrounded && velY < 0f) velY = -2f;
        if (cc.isGrounded && k.spaceKey.wasPressedThisFrame) velY = Mathf.Sqrt(salto * -2f * gravedad);
        velY += gravedad * Time.deltaTime;

        cc.Move((dir * vel + Vector3.up * velY) * Time.deltaTime);
    }
}
