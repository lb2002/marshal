// A soft round glow on a Quad primitive (an agent's halo): the colour fades out from the centre to the edge.
// One colour per renderer (_Color through a MaterialPropertyBlock), no depth write, so a sphere in front of it hides
// its middle and the glow shows around it. Single-pass instanced stereo ready (Quest).
Shader "Lotse/Glow"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 0.4)
        _Fade ("Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Glow"
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 p : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Fade;
            CBUFFER_END

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.p = v.positionOS.xy * 2; // -1..1 across the quad
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half k = saturate(1 - length(i.p));
                half a = _Color.a * k * k * _Fade;
                clip(a - 0.003);
                return half4(_Color.rgb, a);
            }
            ENDHLSL
        }
    }
}
