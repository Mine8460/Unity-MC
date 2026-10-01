Shader "Voxel/PixelShadowLit"
{
    Properties
    {
        [MainTexture] _BaseMap ("Atlas", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        _PixelsPerBlock ("Pixels par bloc", Float) = 16
        _SampleBiasPx ("Bias de lecture (en pixels)", Range(0, 1)) = 0.15
        _Ambient ("Luminosité de l'ombre", Range(0, 1)) = 0.35
        _FaceShade ("Luminosité par face (0 = désactivée)", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float _Cutoff;
            float _PixelsPerBlock;
            float _SampleBiasPx;
            float _Ambient;
            float _FaceShade;
        CBUFFER_END
        ENDHLSL

        // ---------------------------------------------------------------
        // Rendu principal
        // ---------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;       // alpha = mode d'éclairage (voir LightMode)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                half   lightMode  : TEXCOORD3;   // 0 = pixels d'ombre, 0.5 = ombre par bloc, 1 = plein éclat
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.lightMode = IN.color.a;
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            // Luminosité fixe par direction de face, comme dans Minecraft
            float FaceBrightness(float3 n)
            {
                if (n.y > 0.5)  return 1.0;   // dessus
                if (n.y < -0.5) return 0.5;   // dessous
                if (abs(n.z) > 0.5) return 0.8; // faces Z
                return 0.6;                   // faces X
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, IN.uv, 0); // mip 0 : pas de mélange de tuiles à distance
                clip(tex.a - _Cutoff);

                float3 n = normalize(IN.normalWS);

                // --- Ombre pixelisée sur la grille ---
                // 1) on passe en "espace pixels de texture"
                // Doit être identique à UvInsetTexels dans BlockDatabase (C#) :
                // la tuile est rétrécie de uvInset pixel de chaque côté dans les UVs
                const float uvInset = 0.01;
                float T = _PixelsPerBlock;
                float3 blockIdx = floor(IN.positionWS - n * 0.001); // bloc auquel appartient la face
                float3 t = IN.positionWS - blockIdx;                // position dans le bloc, 0..1
                float3 s = t * (T - 2.0 * uvInset) + uvInset;       // position en pixels de texture
                // 2) on prend le centre de la case de pixel (en reculant un peu
                //    dans le bloc pour ne pas hésiter entre deux cases sur la face)
                float3 cell01 = (floor(s) + 0.5 - uvInset) / (T - 2.0 * uvInset);
                float3 cellWS = blockIdx + cell01;
                // 3) sur l'axe de la normale, on reste sur la surface
                cellWS = lerp(cellWS, IN.positionWS, abs(n));
                // 4) retour en unités monde + petit décalage pour éviter l'acné
                float3 posQ = cellWS + n * (_SampleBiasPx / T);

                // Plantes : une seule valeur d'ombre par bloc, lue juste AU-DESSUS de la plante.
                // Leurs plans sont en diagonale : la grille de pixels n'a pas de sens, et lire l'ombre
                // à côté du plan le fait se projeter une ombre à lui-même.
                if (IN.lightMode > 0.25 && IN.lightMode < 0.75)
                {
                    float3 blockCell = floor(IN.positionWS - float3(0.0, 0.001, 0.0));
                    posQ = blockCell + float3(0.5, 1.02, 0.5);
                }

                float4 shadowCoord = TransformWorldToShadowCoord(posQ);
                Light mainLight = GetMainLight(shadowCoord);

                float ndl = saturate(dot(n, mainLight.direction));
                // Tout ou rien : la face regarde le soleil ET n'est pas dans l'ombre => 1, sinon 0
                float lit = step(0.01, ndl) * step(0.5, mainLight.shadowAttenuation);

                float3 lightColor = lerp(_Ambient.xxx, mainLight.color, lit);
                float faceShade = lerp(1.0, FaceBrightness(n), _FaceShade);
                float3 color = tex.rgb * lightColor * faceShade;

                // Torche : couleurs exactes de la texture, ni lumière ni ombre
                if (IN.lightMode > 0.75)
                    color = tex.rgb;

                return half4(color, 1);
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // Les blocs projettent des ombres (feuilles découpées comprises)
        // ---------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            ShadowVaryings ShadowVert(ShadowAttributes IN)
            {
                ShadowVaryings OUT;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 nWS = TransformObjectToWorldNormal(IN.normalOS);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDir = normalize(_LightPosition - posWS);
            #else
                float3 lightDir = _LightDirection;
            #endif

                float4 cs = TransformWorldToHClip(ApplyShadowBias(posWS, nWS, lightDir));
            #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE * cs.w);
            #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE * cs.w);
            #endif

                OUT.positionCS = cs;
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 ShadowFrag(ShadowVaryings IN) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, IN.uv, 0).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
