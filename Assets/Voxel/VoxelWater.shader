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

        // Animation : la texture se décale d'UN pixel à chaque intervalle, en boucle dans sa tuile
        [Header(Animation)]
        _StillStep ("Lac : secondes par pixel de décalage (0 = immobile)", Float) = 0.5
        _FlowStep ("Eau qui coule : secondes par pixel", Float) = 0.12
        _FallStep ("Cascade : secondes par pixel", Float) = 0.05
        _AtlasTiles ("Tuiles par ligne dans l'atlas", Float) = 4
        _PixelsPerTile ("Pixels par tuile", Float) = 16
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
                float _StillStep;
                float _FlowStep;
                float _FallStep;
                float _AtlasTiles;
                float _PixelsPerTile;
            CBUFFER_END

            // Obscurité de la nuit, réglée pour TOUS les matériaux par DayNightCycle (0 = plein jour).
            // Sans DayNightCycle dans la scène, elle vaut 0 : rien ne change.
            float _VoxelDarkness;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;      // r = lumière des torches, g = lumière du ciel
                float2 flow       : TEXCOORD1;  // sens du courant (vers le bas de la pente), zéro sur un lac
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                half2  light      : TEXCOORD3;
                float2 flow       : TEXCOORD4;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.light = IN.color.rg;
                OUT.flow = IN.flow;
                return OUT;
            }

            // Doit rester identique à celle de Voxel/PixelShadowLit : chaque niveau de lumière perd 20 %
            float LightCurve(float level01)
            {
                return pow(0.8, (1.0 - level01) * 15.0);
            }

            // Décale la texture d'un nombre ENTIER de pixels (elle reste nette), en boucle dans sa tuile de l'atlas.
            //  - dessus de l'eau qui coule : vers le bas de la pente ;
            //  - côtés (cascades, bords) : vers le bas ;
            //  - dessus d'un lac : dérive lente.
            float2 AnimatedUV(float2 uv, float3 n, float2 flow)
            {
                float tile = 1.0 / _AtlasTiles;
                float2 origin = floor(uv / tile) * tile;
                float2 local = (uv - origin) / tile;               // 0 à 1 dans la tuile

                float2 shiftPx = 0;                                // décalage de la lecture, en pixels
                if (n.y > 0.5)
                {
                    if (dot(flow, flow) > 1e-4 && _FlowStep > 0)
                        shiftPx = -round(normalize(flow) * floor(_Time.y / _FlowStep)); // le motif avance vers le bas de la pente
                    else if (_StillStep > 0)
                        shiftPx = float2(-floor(_Time.y / _StillStep), 0);
                }
                else if (n.y > -0.5 && _FallStep > 0)
                {
                    shiftPx = float2(0, floor(_Time.y / _FallStep)); // les côtés : le motif descend (v = hauteur)
                }

                return origin + frac(local + shiftPx / _PixelsPerTile) * tile;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 n = normalize(IN.normalWS);
                half4 tex = SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, AnimatedUV(IN.uv, n, IN.flow), 0);

                // Ombre du soleil (tout ou rien, comme les blocs)
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS + n * 0.02);
                Light mainLight = GetMainLight(shadowCoord);
                float lit = step(0.01, saturate(dot(n, mainLight.direction))) * step(0.5, mainLight.shadowAttenuation);
                float3 lightColor = lerp(_Ambient.xxx, max(mainLight.color, _Ambient.xxx), lit); // jamais plus sombre qu'à l'ombre

                float3 skyColor = lightColor * (LightCurve(IN.light.y) * _SkyBrightness * (1.0 - _VoxelDarkness));
                float3 blockColor = _BlockLightColor.rgb * LightCurve(IN.light.x);
                float3 lighting = max(max(skyColor, blockColor), _MinLight.xxx);

                // La transparence vient du matériau : la tuile de l'atlas peut rester opaque
                return half4(tex.rgb * _Tint.rgb * lighting, _Alpha * _Tint.a);
            }
            ENDHLSL
        }
    }
}
