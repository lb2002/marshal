// Unlit colour for the slice's spheres, rings and panels. One colour per renderer (_Color, set through a
// MaterialPropertyBlock), optional fake top light (_Shade) for the spheres. Blend, depth write and culling
// are properties, so one shader serves the opaque and the transparent materials. Shaded objects (the agent
// spheres) also get a soft fresnel rim, so they read as orbs. _Fade scales the alpha (panels fading in).
// Single-pass instanced stereo ready (Quest).
Shader "Lotse/Unlit"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _Shade ("Shade", Range(0, 1)) = 0
        _Fade ("Fade", Range(0, 1)) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Unlit"
            Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
            ZWrite [_ZWrite]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Shade;
                half _Fade;
            CBUFFER_END

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 n = normalize(i.normalWS);
                half light = 0.55h + 0.45h * saturate(dot(n, normalize(half3(0.3h, 1.0h, -0.4h))));
                half3 c = _Color.rgb * lerp(1.0h, light, _Shade);
                half3 view = normalize(GetWorldSpaceViewDir(i.positionWS));
                half rim = pow(1.0h - saturate(dot(n, view)), 3.0h) * _Shade;
                c += rim * (0.45h + 0.55h * c);
                return half4(c, _Color.a * _Fade);
            }
            ENDHLSL
        }
    }
}
