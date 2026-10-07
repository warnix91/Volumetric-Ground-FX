Shader "Hidden/GroundBlastFx/CompatibilityDepth"
{
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float4 frag(v2f_img i) : SV_Target
            {
                return float4(LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv)), 0, 0, 1);
            }
            ENDCG
        }
    }
}
