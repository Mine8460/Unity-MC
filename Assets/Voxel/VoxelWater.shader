Shader "Voxel/Water"
{
    // Eau des chunks (sous-mesh 1). Même éclairage que Voxel/PixelShadowLit : lumière du ciel et des torches
    // lue dans les couleurs de sommets, ombre du soleil. Mais transparente, et visible des deux côtés
    // (on voit la surface depuis le fond).
    Properties
    {
        [MainTexture] _BaseMap ("Atlas", 2D) = "white" {}
        _Tint ("Teinte", Color) = (1, 1, 1, 1)
        _Alpha ("Opacité", Range(0, 1)) = 0.7
        _Ambient ("Luminosité de l'ombre", Range(0, 1)) = 0.35
        _SkyBrightness ("Luminosité du ciel (1 = jour, 0.1 = nuit)", Range(0, 1)) = 1
        _MinLight ("Lumière minimale", Range(0, 0.2)) = 0.03
        _BlockLightColor ("Couleur de la lumière des torches", Color) = (1, 0.82, 0.55, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _Tint;
                float _Alpha;
                float _Ambient;
                float _SkyBrightness;
                float _MinLight;
                half4 _BlockLightColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;      // r = lumière des torches, g = lumière du ciel
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                half2  light      : TEXCOORD3;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.light = IN.color.rg;
                return OUT;
            }

            // Doit rester identique à celle de Voxel/PixelShadowLit : chaque niveau de lumière perd 20 %
            float LightCurve(float level01)
            {
                return pow(0.8, (1.0 - level01) * 15.0);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, IN.uv, 0);
                float3 n = normalize(IN.normalWS);

                // Ombre du soleil (tout ou rien, comme les blocs)
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS + n * 0.02);
                Light mainLight = GetMainLight(shadowCoord);
                float lit = step(0.01, saturate(dot(n, mainLight.direction))) * step(0.5, mainLight.shadowAttenuation);
                float3 lightColor = lerp(_Ambient.xxx, mainLight.color, lit);

                float3 skyColor = lightColor * (LightCurve(IN.light.y) * _SkyBrightness);
                float3 blockColor = _BlockLightColor.rgb * LightCurve(IN.light.x);
                float3 lighting = max(max(skyColor, blockColor), _MinLight.xxx);

                // La transparence vient du matériau : la tuile de l'atlas peut rester opaque
                return half4(tex.rgb * _Tint.rgb * lighting, _Alpha * _Tint.a);
            }
            ENDHLSL
        }
    }
}
