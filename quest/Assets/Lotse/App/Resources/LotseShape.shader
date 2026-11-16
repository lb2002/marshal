// The slice's panels, buttons, badges and dots: a rounded rectangle drawn by a signed distance field on a Quad
// primitive, with a soft drop shadow (its colour and drop set per look; centred and coloured it is a glow ring), a rim light along the edge (stronger at the top), a subtle vertical gradient
// and an anti-aliased edge. The rectangle is the quad's own size in metres (read from its world scale), and the
// vertex shader grows the quad outward by the shadow's margin, so a panel keeps its size and anchor while its
// shadow falls outside. The corner radius is in metres and clamped to half the short side (a large radius gives a
// pill, a square gives a circle).
// Two passes over disjoint pixels: the shadow outside the shape (no depth written, so it never hides what lies
// behind it) and the shape itself (depth written when _ZWrite is on, so text behind a panel stays hidden).
// One material per style (Theme); colour, fade and highlight come per renderer through a MaterialPropertyBlock.
// Single-pass instanced stereo ready (Quest).
Shader "Lotse/Shape"
{
    Properties
    {
        _Color ("Color", Color) = (0.055, 0.08, 0.15, 0.85)
        _Radius ("Corner radius (m)", Float) = 0.012
        _Rim ("Rim light", Range(0, 1)) = 0.35
        _Gradient ("Vertical gradient", Range(0, 1)) = 0.25
        _Shadow ("Shadow alpha", Range(0, 1)) = 0.35
        _ShadowSize ("Shadow size (m)", Float) = 0.012
        _ShadowColor ("Shadow colour", Color) = (0, 0, 0, 1)
        _ShadowOffset ("Shadow drop (x size)", Float) = 0.35
        _RimColor ("Rim colour", Color) = (0.62, 0.74, 1, 1)
        _RimWidth ("Rim width (m)", Float) = 0.0008
        _Highlight ("Highlight", Range(0, 1)) = 0
        _Fade ("Fade", Range(0, 1)) = 1
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct Attributes
        {
            float4 positionOS : POSITION;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            // x, y: the point in metres from the centre; z, w: half the rectangle's size in metres
            float4 rect : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        CBUFFER_START(UnityPerMaterial)
            half4 _Color;
            float _Radius;
            half _Rim;
            half _Gradient;
            half _Shadow;
            float _ShadowSize;
            half4 _ShadowColor;
            float _ShadowOffset;
            half4 _RimColor;
            float _RimWidth;
            half _Highlight;
            half _Fade;
        CBUFFER_END

        Varyings vert(Attributes v)
        {
            Varyings o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float3x3 m = (float3x3)GetObjectToWorldMatrix();
            float2 size = max(float2(length(float3(m[0][0], m[1][0], m[2][0])), length(float3(m[0][1], m[1][1], m[2][1]))), 1e-5);
            float margin = _ShadowSize * 1.5 + 0.002;
            float2 p = v.positionOS.xy * size + sign(v.positionOS.xy) * margin;
            o.positionCS = TransformObjectToHClip(float3(p / size, 0));
            o.rect = float4(p, size * 0.5);
            return o;
        }

        float RoundBox(float2 p, float2 halfSize, float r)
        {
            float2 q = abs(p) - halfSize + r;
            return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
        }

        // how much of this pixel the shape covers (anti-aliased), and its signed distance
        float Cover(float4 rect, out float d, out float r)
        {
            r = min(_Radius, min(rect.z, rect.w));
            d = RoundBox(rect.xy, rect.zw, r);
            float aa = max(fwidth(d), 1e-5);
            return 1 - smoothstep(-aa, aa, d);
        }
        ENDHLSL

        // the drop shadow, outside the shape only; never writes depth
        Pass
        {
            Name "Shadow"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragShadow
            #pragma multi_compile_instancing

            half4 fragShadow(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float d, r;
                float cover = Cover(i.rect, d, r);
                if (d < -0.001) discard; // inside the shape: the shape pass draws it
                float ds = RoundBox(i.rect.xy - float2(0, -_ShadowSize * _ShadowOffset), i.rect.zw, r);
                half a = _Shadow * (1 - smoothstep(-_ShadowSize * 0.4, _ShadowSize, ds)) * (1 - cover) * _Fade;
                clip(a - 0.004);
                return half4(_ShadowColor.rgb, a);
            }
            ENDHLSL
        }

        // the shape: glass fill with a gradient and a rim light; writes depth where it is (when _ZWrite is on)
        Pass
        {
            Name "Shape"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite [_ZWrite]
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragShape
            #pragma multi_compile_instancing

            half4 fragShape(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float d, r;
                float cover = Cover(i.rect, d, r);
                clip(cover - 0.01);
                // 0 at the bottom edge, 1 at the top
                float top = saturate(i.rect.y / (2 * i.rect.w) + 0.5);
                half3 col = _Color.rgb * (1 + _Gradient * (top - 0.4));
                // a crisp hairline along the inside of the edge in the rim colour (U9: a border, not a bevel); lighter when highlighted
                float aa = max(fwidth(d), 1e-5);
                float rim = (1 - smoothstep(_RimWidth - aa, _RimWidth + aa, -d)) * saturate(_Rim);
                half3 rimCol = lerp(_RimColor.rgb, half3(1, 1, 1), _Highlight * 0.45);
                col = lerp(col, rimCol, rim);
                col += _Highlight * 0.03;
                half a = saturate(_Color.a + rim) * cover * _Fade;
                clip(a - 0.003); // a fully faded shape hides nothing
                return half4(col, a);
            }
            ENDHLSL
        }
    }
}
