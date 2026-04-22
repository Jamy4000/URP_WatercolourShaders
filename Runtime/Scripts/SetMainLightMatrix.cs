using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class SetMainLightMatrix : MonoBehaviour {
    
    private readonly int _property = Shader.PropertyToID("_WorldToMainLightMatrix");
    
    void Update() {
        // Create Rotation Matrix from transform.
        // Basically transform.localToWorldMatrix, but only rotation.
        Matrix4x4 matrix = Matrix4x4.Rotate(transform.rotation);

        // Set Global Matrix shader property
        Shader.SetGlobalMatrix(_property, matrix);
    }
}
