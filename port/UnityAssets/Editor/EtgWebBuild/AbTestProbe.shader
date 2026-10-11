// Diagnostic for port/abtest.sh: a 4x4 grid of cells, each showing what the engine feeds a built-in uniform on
// this graphics API (value * 0.5 + 0.5 where it can be negative). Expected to differ between D3D11 and GLES in the
// cells marked (API); everything else differing means the engine sets the uniform differently than assumed.
//  row 0 (bottom): _Time | _SinTime | unity_DeltaTime | _ProjectionParams (API: x = -1 on D3D render textures)
//  row 1: _ScreenParams | _ZBufferParams (API) | unity_OrthoParams | clip-space xy of the fragment (API: y flip)
//  row 2: SV_Position / _ScreenParams | defines: UV_STARTS_AT_TOP, REVERSED_Z, GLES3 (API) | _WorldSpaceCameraPos | ambient
//  row 3: object-to-clip row 1 (API) | UNITY_NEAR_CLIP_VALUE (API) | uv | white
Shader "Hidden/EtgAbTest/Probe" {
    SubShader {
        Pass {
            ZWrite Off ZTest Always Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 clip : TEXCOORD1;
            };

            v2f vert(appdata_base v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.clip = o.pos;
                o.uv = v.texcoord.xy;
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                int cell = (int)floor(i.uv.x * 4) + 4 * (int)floor(i.uv.y * 4);
                float4 c = float4(0, 0, 0, 1);
                if (cell == 0) c.rgb = frac(_Time.xyz * float3(1, 0.1, 0.01));
                else if (cell == 1) c.rgb = _SinTime.xyz * 0.5 + 0.5;
                else if (cell == 2) c.rgb = saturate(unity_DeltaTime.xyz * float3(10, 0.01, 10));
                else if (cell == 3) c.rgb = float3(_ProjectionParams.x * 0.5 + 0.5, _ProjectionParams.y, _ProjectionParams.z * 0.1);
                else if (cell == 4) c.rgb = float3(_ScreenParams.xy / 1024, _ScreenParams.z - 1);
                else if (cell == 5) c.rgb = _ZBufferParams.xyz * 0.05 + 0.5;
                else if (cell == 6) c.rgb = unity_OrthoParams.xyw;
                else if (cell == 7) c.rgb = float3(i.clip.xy / i.clip.w * 0.5 + 0.5, 0);
                else if (cell == 8) c.rgb = float3(i.pos.xy / _ScreenParams.xy, 0);
                else if (cell == 9) {
                    #if UNITY_UV_STARTS_AT_TOP
                    c.r = 1;
                    #endif
                    #if defined(UNITY_REVERSED_Z)
                    c.g = 1;
                    #endif
                    #if defined(SHADER_API_GLES3)
                    c.b = 1;
                    #endif
                }
                else if (cell == 10) c.rgb = frac(_WorldSpaceCameraPos * 0.25 + 0.5);
                else if (cell == 11) c.rgb = UNITY_LIGHTMODEL_AMBIENT.rgb;
                else if (cell == 12) c.rgb = UNITY_MATRIX_VP[1].xyz * 0.5 + 0.5;
                else if (cell == 13) c.rgb = UNITY_NEAR_CLIP_VALUE * 0.5 + 0.5;
                else if (cell == 14) c.rgb = float3(i.uv, 0);
                else c.rgb = 1;
                return c;
            }
            ENDCG
        }
    }
}
