Shader "Hidden/EtgAbTest/LightProbe" {
    // Diagnostic for port/abtest.sh: the light data the forward renderer feeds a lit shader on this graphics API.
    // Lower half (ForwardBase), 4 cells per row, row 0 at the bottom:
    //  row 0: _LightColor0 | _WorldSpaceLightPos0 | ShadeSH9(normal) | keywords VERTEXLIGHT_ON, LIGHTPROBE_SH, DIRECTIONAL
    //  row 1: unity_4LightPosX0 | unity_4LightAtten0 | unity_LightColor[0] | unity_LightColor[1]
    // Upper half (ForwardAdd, one pass per pixel light, additive): POINT -> row 2, SPOT -> row 3, cookies -> columns 2-3
    //  each: _LightColor0 | LIGHT_ATTENUATION at the fragment
    Properties { }
    SubShader {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Pass {
            Tags { "LightMode"="ForwardBase" }
            ZWrite On Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            struct v2f {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : TEXCOORD1;
            };

            v2f vert(appdata_base v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                if (i.uv.y >= 0.5) return float4(0, 0, 0, 1);
                int cell = (int)floor(i.uv.x * 4) + 4 * (int)floor(i.uv.y * 4);
                float4 c = float4(0, 0, 0, 1);
                if (cell == 0) c.rgb = _LightColor0.rgb * 0.5;
                else if (cell == 1) c.rgb = _WorldSpaceLightPos0.xyz * 0.5 + 0.5;
                else if (cell == 2) c.rgb = ShadeSH9(float4(i.normal, 1));
                else if (cell == 3) {
                    #ifdef VERTEXLIGHT_ON
                    c.r = 1;
                    #endif
                    #ifdef LIGHTPROBE_SH
                    c.g = 1;
                    #endif
                    #ifdef DIRECTIONAL
                    c.b = 1;
                    #endif
                }
                else if (cell == 4) c.rgb = unity_4LightPosX0.xyz * 0.5 + 0.5;
                else if (cell == 5) c.rgb = unity_4LightAtten0.xyz * 0.1;
                else if (cell == 6) c.rgb = unity_LightColor[0].rgb * 0.5;
                else c.rgb = unity_LightColor[1].rgb * 0.5;
                return c;
            }
            ENDCG
        }
        Pass {
            Tags { "LightMode"="ForwardAdd" }
            ZWrite Off Blend One One Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdadd
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            struct v2f {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
            };

            v2f vert(appdata_base v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                int col = (int)floor(i.uv.x * 4);
                int row = (int)floor(i.uv.y * 4);
                int wantRow = 2, wantCol = 0;
                #if defined(SPOT)
                wantRow = 3;
                #endif
                #if defined(POINT_COOKIE) || defined(DIRECTIONAL_COOKIE)
                wantCol = 2;
                #endif
                #if defined(DIRECTIONAL) || defined(DIRECTIONAL_COOKIE)
                wantRow = 3; wantCol = 2;
                #endif
                if (row != wantRow || (col != wantCol && col != wantCol + 1)) return 0;
                UNITY_LIGHT_ATTENUATION(atten, 0, i.worldPos);
                if (col == wantCol) return float4(_LightColor0.rgb * 0.5, 0);
                return float4(atten.xxx, 0);
            }
            ENDCG
        }
    }
}
