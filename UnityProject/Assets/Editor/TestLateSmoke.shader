// GroundBlastFx — banc de rendu uniquement (pas dans le bundle du mod). Imite la fumée d'un autre mod dessinée après nos
// nuages et calée sur _CameraDepthTexture (comme Raymarched Plume Trails) : un bandeau bleu à une distance fixe, caché
// seulement par ce que contient la carte de profondeur. Activé par GE_TEST_LATESMOKE=1.
Shader "Hidden/GE TestLateSmoke"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D_float _CameraDepthTexture;
            float _GETestWallDepth;   // distance (profondeur caméra, m) du bandeau

            fixed4 frag(v2f_img i) : SV_Target
            {
                if (i.uv.y < 0.3 || i.uv.y > 0.62) return 0;
                float raw = tex2D(_CameraDepthTexture, i.uv).r;
                #if defined(UNITY_REVERSED_Z)
                float eye = raw < 0.0001 ? 1e6 : LinearEyeDepth(raw);
                #else
                float eye = raw > 0.9999 ? 1e6 : LinearEyeDepth(raw);
                #endif
                return eye > _GETestWallDepth ? fixed4(0.12, 0.2, 0.7, 0.8) : 0;
            }
            ENDCG
        }
    }
}
