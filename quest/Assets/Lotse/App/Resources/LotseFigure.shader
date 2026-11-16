// The little workers (U4a review, I3): one opaque unlit material for every figure, the colours in the vertices, so a
// worker is a handful of draw calls (its body and head baked into one mesh, its two arms, its prop) and needs no
// MaterialPropertyBlock per part. A gentle top light and a soft fresnel rim make the parts read round. _Fade lets a
// panel's appearance fade the figure in (Appear). Single-pass instanced stereo ready (Quest).
Shader "Lotse/Figure"
{
    Properties
    {
        _Shade ("Shade", Range(0, 1)) = 0.55
        _Fade ("Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Figure"
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
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
                o.color = v.color;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 n = normalize(i.normalWS);
                half light = 0.55h + 0.45h * saturate(dot(n, normalize(half3(0.3h, 1.0h, -0.4h))));
                half3 c = i.color.rgb * lerp(1.0h, light, _Shade);
                half3 view = normalize(GetWorldSpaceViewDir(i.positionWS));
                half rim = pow(1.0h - saturate(dot(n, view)), 3.0h) * _Shade;
                c += rim * (0.45h + 0.55h * c);
                // fading in: dissolve the opaque figure with a fine screen-space pattern (no sorting needed)
                if (_Fade < 0.999h)
                {
                    float2 p = floor(i.positionCS.xy);
                    half pattern = frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
                    clip(_Fade - pattern);
                }
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
