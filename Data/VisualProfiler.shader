// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

Shader "Hidden/Visual Profiler"
{
    Properties
    {
        [MainTexture] _FontTexture("Font", 2D) = "black" {}
    }

    SubShader
    {
        Pass
        {
            Name "Main"
            Tags{ "RenderType" = "Opaque" }
            Blend Off
            ZWrite On
            ZTest Always
            Cull Off

            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            // Comment in to help with RenderDoc debugging.
            //#pragma enable_d3d11_debug_symbols

            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed3 color : COLOR0;
                fixed3 baseColor : COLOR1;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _FontTexture;
            float2 _FontScale;
            float4x4 _WindowLocalToWorldMatrix;

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _BaseColor)
                UNITY_DEFINE_INSTANCED_PROP(float4, _UVOffsetScaleX)
            UNITY_INSTANCING_BUFFER_END(Props)

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float4 uvOffsetScaleX = UNITY_ACCESS_INSTANCED_PROP(Props, _UVOffsetScaleX);

                // The verticies on the right (UV 1, x) are scaled in the positive X direction for progress bars.
                float3 localVertex = v.vertex.xyz;
                localVertex.x += v.uv.x * uvOffsetScaleX.z;

                // Convert from window (local) to world space.
                // We do this in the vertex shader to avoid having to iterate over all instances each frame.
                o.vertex = mul(UNITY_MATRIX_VP, mul(_WindowLocalToWorldMatrix, mul(unity_ObjectToWorld, float4(localVertex, 1.0))));
                
                // MaterialPropertyBlocks do not have a SetColorArray method, so we need to do color space conversions ourselves.
#if defined(UNITY_COLORSPACE_GAMMA)
                o.color = UNITY_ACCESS_INSTANCED_PROP(Props, _Color).rgb;
                o.baseColor = UNITY_ACCESS_INSTANCED_PROP(Props, _BaseColor).rgb;
#else
                o.color = GammaToLinearSpace(UNITY_ACCESS_INSTANCED_PROP(Props, _Color).rgb);
                o.baseColor = GammaToLinearSpace(UNITY_ACCESS_INSTANCED_PROP(Props, _BaseColor).rgb);
#endif

                // Scale and offset UVs.
                o.uv = (v.uv * _FontScale) + uvOffsetScaleX.xy;

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 font = tex2D(_FontTexture, i.uv);
                fixed alpha = font.r;
                return fixed4((i.baseColor * (1.0 - alpha)) + (font.rgb * i.color * alpha), 1.0);
            }

            ENDCG
        }
    }
}
