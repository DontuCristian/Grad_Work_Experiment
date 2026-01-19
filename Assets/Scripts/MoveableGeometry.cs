using System.Collections;
using UnityEngine;

public class MoveableGeometry : MonoBehaviour
{
    [SerializeField] private float _amplitude = 1.0f;   // meters
    private float _slowSpeed = 0.5f;   // Hz
    private float _fastSpeed = 2.0f;   // Hz

    private Coroutine _moveCoroutine;
    private bool _isMovingSlow = false;
    private Vector3 _startPos;

    private void Start()
    {
        _startPos = transform.position;
    }

    // Update is called once per frame
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            StartSideToSide(_slowSpeed);
            _isMovingSlow = true;
        }

        if (Input.GetKeyDown(KeyCode.R))
        {
            StartSideToSide(_fastSpeed);
            _isMovingSlow = false;
        }
    }

    void StartSideToSide(float speed)
    {
        if (_moveCoroutine != null)
            StopCoroutine(_moveCoroutine);

        transform.position = _startPos;
        _moveCoroutine = StartCoroutine(SideToSideCoroutine(speed));
    }

    IEnumerator SideToSideCoroutine(float maxSpeed)
    {
        float t = 0f;

        while (true)
        {
            t += Time.deltaTime * maxSpeed / _amplitude;
            float offset = Mathf.Sin(t) * _amplitude;

            transform.position = _startPos + Vector3.right * offset;
            yield return null;
        }
    }

}
