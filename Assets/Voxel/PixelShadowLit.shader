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
        _SkyBrightness ("Luminosité du ciel (1 = jour, 0.1 = nuit)", Range(0, 1)) = 1
        _MinLight ("Lumière minimale", Range(0, 0.2)) = 0.03
        _BlockLightColor ("Couleur de la lumière des torches", Color) = (1, 0.82, 0.55, 1)

        // Les textures ci-dessous sont des ATLAS rangés exactement comme l'atlas principal
        // (même nombre de tuiles, même taille). Une case cochée active la texture ; décochée = rendu d'origine.
        [Header(Height map (relief))]
        [Toggle(_PARALLAXMAP)] _UseHeightMap ("Utiliser la height map", Float) = 0
        [NoScaleOffset] _HeightMap ("Height map (atlas, gris : blanc = haut)", 2D) = "white" {}
        _ParallaxDepth ("Profondeur du relief (en blocs, 0.0625 = 1 pixel)", Range(0, 0.25)) = 0.0625
        _ParallaxShadow ("Ombre du relief", Range(0, 1)) = 1
        _WallShade ("Luminosité des parois du relief", Range(0, 1)) = 0.8
        _AtlasTiles ("Tuiles par ligne dans l'atlas", Float) = 4

        [Header(Metallic)]
        [Toggle(_METALLICMAP)] _UseMetallicMap ("Utiliser la metallic map", Float) = 0
        [NoScaleOffset] _MetallicGlossMap ("Metallic (R) et lissé (A) (atlas)", 2D) = "black" {}
        _Metallic ("Multiplicateur metallic", Range(0, 1)) = 1
        _Smoothness ("Multiplicateur lissé", Range(0, 1)) = 1
        _ReflectionStrength ("Force des reflets du ciel", Range(0, 2)) = 1

        [Header(Emission)]
        [Toggle(_EMISSION)] _UseEmission ("Utiliser l'emissive map", Float) = 0
        [NoScaleOffset] _EmissionMap ("Emissive (atlas)", 2D) = "black" {}
        [HDR] _EmissionColor ("Couleur et intensité d'émission", Color) = (1, 1, 1, 1)
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
        SAMPLER(sampler_BaseMap);   // filtrage point : réutilisé pour TOUS les atlas (pas de flou, pas de mélange de tuiles)
        TEXTURE2D(_HeightMap);
        TEXTURE2D(_MetallicGlossMap);
        TEXTURE2D(_EmissionMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float _Cutoff;
            float _PixelsPerBlock;
            float _SampleBiasPx;
            float _Ambient;
            float _FaceShade;
            float _SkyBrightness;
            float _MinLight;
            half4 _BlockLightColor;
            float _ParallaxDepth;
            float _ParallaxShadow;
            float _WallShade;
            float _AtlasTiles;
            float _Metallic;
            float _Smoothness;
            float _ReflectionStrength;
            half4 _EmissionColor;
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
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _METALLICMAP
            #pragma shader_feature_local _EMISSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"

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
                half2  light      : TEXCOORD4;   // x = lumière des torches, y = lumière du ciel (0 à 1 pour les niveaux 0 à 15)
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.lightMode = IN.color.a;
                OUT.light = IN.color.rg;
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

            // Luminosité d'un niveau de lumière (0 à 1 pour les niveaux 0 à 15) : chaque niveau perd 20 %, comme Minecraft
            float LightCurve(float level01)
            {
                return pow(0.8, (1.0 - level01) * 15.0);
            }

            // Repère de la face : directions (unitaires, en monde) des u et des v croissants, et nombre d'unités
            // d'UV par unité monde. Le mesh n'a pas de tangentes : on les déduit des dérivées de la position
            // et des UV, ce qui suit n'importe quelle orientation d'UV (faces en miroir, modèles Blockbench...).
            void FaceFrame(float3 n, float3 positionWS, float2 uv, out float3 tU, out float3 tV, out float uvPerUnit)
            {
                float3 dp1 = ddx(positionWS);
                float3 dp2 = ddy(positionWS);
                float2 duv1 = ddx(uv);
                float2 duv2 = ddy(uv);

                float3 dp2perp = cross(dp2, n);
                float3 dp1perp = cross(n, dp1);
                float3 t = dp2perp * duv1.x + dp1perp * duv2.x;
                float3 b = dp2perp * duv1.y + dp1perp * duv2.y;

                // det : son signe corrige la convention de l'écran (sans lui, une face sur deux serait inversée),
                // sa valeur ramène les vecteurs à de vrais gradients (UV par unité monde)
                float det = dot(dp1, dp2perp);
                float s = det < 0.0 ? -1.0 : 1.0;
                float lt = length(t), lb = length(b);
                tU = t * (s / max(lt, 1e-12));
                tV = b * (s / max(lb, 1e-12));
                uvPerUnit = 0.5 * (lt + lb) / max(abs(det), 1e-12);
            }

            // Ramène un texel dans sa tuile : au-delà du bord d'un bloc, le rayon continue sur le pixel du bord,
            // prolongé (pas de mur au bord, et jamais de pixel pris de l'autre côté de la tuile : sur la face
            // d'un bloc d'herbe, le haut reste de l'herbe et le bas de la terre).
            float2 WrapCell(float2 cell, float2 tileMin)
            {
                return clamp(cell, tileMin, tileMin + _PixelsPerBlock - 1.0);
            }

            // Hauteur (0 = bas, 1 = haut) d'un texel de la tuile, repéré par ses coordonnées entières
            float TexelHeight(float2 cell, float2 tileMin, float texelUV)
            {
                return SAMPLE_TEXTURE2D_LOD(_HeightMap, sampler_BaseMap, (WrapCell(cell, tileMin) + 0.5) * texelUV, 0).r;
            }

            // Lancer de rayon TEXEL PAR TEXEL dans la height map : chaque pixel est un petit pavé, avec un dessus
            // et des parois nettes. Tout est en unités de texels.
            //   tc     : point d'entrée sur la face ;  dir : déplacement par unité de profondeur
            //   depthT : profondeur totale du relief ; tileMin : premier texel de la tuile (le rayon boucle dedans)
            // Sorties : point touché, profondeur, texel touché, et normale (0,0,1 = dessus ; sinon une paroi).
            void TraceRelief(float2 tc, float2 dir, float depthT, float2 tileMin, float texelUV,
                             out float2 hitTc, out float hitZ, out float2 hitCell, out float3 normalTS)
            {
                float2 cell = floor(tc);
                float2 stepDir = float2(dir.x >= 0.0 ? 1.0 : -1.0, dir.y >= 0.0 ? 1.0 : -1.0);
                float2 inv = float2(abs(dir.x) > 1e-6 ? 1.0 / abs(dir.x) : 1e30, abs(dir.y) > 1e-6 ? 1.0 / abs(dir.y) : 1e30);
                // profondeur à laquelle le rayon atteint la prochaine frontière de texel, en x et en y
                float2 nextZ = float2(
                    (stepDir.x > 0.0 ? cell.x + 1.0 - tc.x : tc.x - cell.x) * inv.x,
                    (stepDir.y > 0.0 ? cell.y + 1.0 - tc.y : tc.y - cell.y) * inv.y);

                float zEnter = 0.0;
                normalTS = float3(0, 0, 1);
                hitTc = tc; hitZ = 0.0; hitCell = cell;

                [loop] for (int i = 0; i < 48; i++)
                {
                    float surface = (1.0 - TexelHeight(cell, tileMin, texelUV)) * depthT; // profondeur du dessus de ce texel
                    float zExit = min(nextZ.x, nextZ.y);

                    if (surface <= zExit || i == 47)
                    {
                        // Le rayon touche ce texel : par le côté s'il était déjà plus haut que le rayon à l'entrée
                        if (surface < zEnter) hitZ = zEnter;
                        else { hitZ = surface; normalTS = float3(0, 0, 1); }
                        // Résultat ramené dans la tuile (le rayon a pu continuer au-delà du bord)
                        hitCell = WrapCell(cell, tileMin);
                        hitTc = clamp(tc + dir * hitZ, cell + 0.001, cell + 0.999) + (hitCell - cell);
                        return;
                    }

                    // Texel suivant (et la paroi par laquelle on y entrerait)
                    if (nextZ.x < nextZ.y) { cell.x += stepDir.x; normalTS = float3(-stepDir.x, 0, 0); zEnter = nextZ.x; nextZ.x += inv.x; }
                    else                   { cell.y += stepDir.y; normalTS = float3(0, -stepDir.y, 0); zEnter = nextZ.y; nextZ.y += inv.y; }
                }
            }

            // Ombre du relief : on remonte du point touché vers le soleil. 0 si un texel plus haut bloque le soleil.
            //   dirL : déplacement (en texels) par unité de montée vers le soleil
            float ReliefShadow(float2 p, float z, float2 cell, float2 dirL, float depthT, float2 tileMin, float texelUV)
            {
                float2 stepDir = float2(dirL.x >= 0.0 ? 1.0 : -1.0, dirL.y >= 0.0 ? 1.0 : -1.0);
                float2 inv = float2(abs(dirL.x) > 1e-6 ? 1.0 / abs(dirL.x) : 1e30, abs(dirL.y) > 1e-6 ? 1.0 / abs(dirL.y) : 1e30);
                float2 nextR = float2(
                    (stepDir.x > 0.0 ? cell.x + 1.0 - p.x : p.x - cell.x) * inv.x,
                    (stepDir.y > 0.0 ? cell.y + 1.0 - p.y : p.y - cell.y) * inv.y);

                [loop] for (int i = 0; i < 48; i++)
                {
                    float rise = min(nextR.x, nextR.y);
                    if (z - rise <= 0.0) return 1.0;                   // sorti par le dessus du relief : au soleil

                    if (nextR.x < nextR.y) { cell.x += stepDir.x; nextR.x += inv.x; }
                    else                   { cell.y += stepDir.y; nextR.y += inv.y; }

                    if (z - rise > (1.0 - TexelHeight(cell, tileMin, texelUV)) * depthT + 1e-4) return 0.0; // sous un texel plus haut
                }
                return 1.0;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 n = normalize(IN.normalWS);
                float2 uv = IN.uv;     // UV où lire les textures (déplacées par le relief)
                float3 nd = n;         // normale du point vu (dessus ou paroi d'un pixel du relief)

            #if defined(_PARALLAXMAP)
                // Repère calculé hors de tout "if" : les dérivées l'exigent
                float3 tU, tV; float uvPerUnit;
                FaceFrame(n, IN.positionWS, IN.uv, tU, tV, uvPerUnit);

                float texelUV = 1.0 / (_AtlasTiles * _PixelsPerBlock);   // taille d'un texel en UV
                float depthT = _ParallaxDepth * uvPerUnit / texelUV;      // profondeur du relief, en texels
                bool relief = IN.lightMode < 0.25 && depthT > 1e-4;       // ni les plantes, ni la torche
                float2 hitTc = 0, hitCell = 0, tileMin = 0;
                float hitZ = 0;
                float3 normalTS = float3(0, 0, 1);

                if (relief)
                {
                    float3 V = normalize(_WorldSpaceCameraPos.xyz - IN.positionWS);
                    float3 viewTS = float3(dot(V, tU), dot(V, tV), max(dot(V, n), 0.1));
                    float2 tc = IN.uv / texelUV;
                    tileMin = floor(tc / _PixelsPerBlock) * _PixelsPerBlock;

                    TraceRelief(tc, -viewTS.xy / viewTS.z, depthT, tileMin, texelUV, hitTc, hitZ, hitCell, normalTS);
                    uv = hitTc * texelUV;
                    nd = normalize(normalTS.x * tU + normalTS.y * tV + normalTS.z * n);
                }
            #endif

                half4 tex = SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, uv, 0); // mip 0 : pas de mélange de tuiles à distance
                clip(tex.a - _Cutoff);

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
                float faceShade = lerp(1.0, FaceBrightness(n), _FaceShade);

                float wallShade = 1.0;
            #if defined(_PARALLAXMAP)
                if (relief)
                {
                    float3 L = mainLight.direction;
                    // Une paroi tournée à l'opposé du soleil est à l'ombre
                    lit *= step(0.01, dot(nd, L));

                    // Un pixel plus haut peut cacher le soleil : ombre nette du relief
                    float3 lightTS = float3(dot(L, tU), dot(L, tV), dot(L, n));
                    if (lit > 0.0 && lightTS.z > 0.01)
                    {
                        float sh = ReliefShadow(hitTc, hitZ, hitCell, lightTS.xy / lightTS.z, depthT, tileMin, texelUV);
                        lit *= lerp(1.0, sh, _ParallaxShadow);
                    }

                    // Parois un peu plus sombres, même à l'ombre : le relief se lit toujours
                    if (normalTS.z < 0.5) wallShade = _WallShade;
                }
            #endif

                float3 lightColor = lerp(_Ambient.xxx, mainLight.color, lit);

                // Lumière par bloc : le ciel (soleil et ombres du soleil, atténués par le niveau de ciel)
                // et les torches (lumière chaude). On garde la plus forte des deux.
                float skyLevel = LightCurve(IN.light.y) * _SkyBrightness;
                float3 skyColor = lightColor * skyLevel;
                float3 blockColor = _BlockLightColor.rgb * LightCurve(IN.light.x);
                float3 lighting = max(max(skyColor, blockColor), _MinLight.xxx);

                float3 albedo = tex.rgb;
                float3 color;

            #if defined(_METALLICMAP)
                // --- Métal et brillance ---
                // Metallic (R) : 1 = métal (sa couleur passe dans les reflets). Lissé (A) : 1 = miroir.
                // Les deux à 0 donnent exactement le rendu d'origine.
                half4 mg = SAMPLE_TEXTURE2D_LOD(_MetallicGlossMap, sampler_BaseMap, uv, 0);
                float metallic = mg.r * _Metallic;
                float smoothness = mg.a * _Smoothness;

                color = albedo * (1.0 - metallic) * lighting * faceShade * wallShade;

                float3 V = normalize(_WorldSpaceCameraPos.xyz - IN.positionWS);
                float3 specColor = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);
                float perceptualRoughness = 1.0 - smoothness;
                float roughness = max(perceptualRoughness * perceptualRoughness, 0.002);

                // Reflet du soleil (même formule que l'éclairage direct d'URP), seulement au soleil
                float3 L = mainLight.direction;
                float3 H = SafeNormalize(L + V);
                float NoH = saturate(dot(nd, H));
                float LoH = saturate(dot(L, H));
                float NoL = saturate(dot(nd, L));
                float a2 = roughness * roughness;
                float d = NoH * NoH * (a2 - 1.0) + 1.00001;
                float specTerm = a2 / ((d * d) * max(0.1, LoH * LoH) * (roughness * 4.0 + 2.0));
                color += specColor * specTerm * mainLight.color * NoL * lit * skyLevel * smoothness;

                // Reflet du ciel (sonde de réflexion ou skybox), éteint là où le ciel ne passe pas (grottes)
                float3 R = reflect(-V, nd);
                half4 encoded = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, R,
                                                        PerceptualRoughnessToMipmapLevel(perceptualRoughness));
                float3 env = DecodeHDREnvironment(encoded, unity_SpecCube0_HDR);
                float NoV = saturate(dot(nd, V));
                float3 fresnel = specColor + (max(smoothness.xxx, specColor) - specColor) * pow(1.0 - NoV, 5.0);
                color += env * fresnel * skyLevel * smoothness * _ReflectionStrength;
            #else
                color = albedo * lighting * faceShade * wallShade;
            #endif

                // Torche : couleurs exactes de la texture, ni lumière ni ombre
                if (IN.lightMode > 0.75)
                    color = tex.rgb;

            #if defined(_EMISSION)
                // Émission : brille même dans le noir. (N'éclaire PAS les blocs voisins : pour ça, il y a
                // la lumière des blocs, BlockInfo.emission.) Avec le Bloom d'URP, une intensité > 1 fait un halo.
                color += SAMPLE_TEXTURE2D_LOD(_EmissionMap, sampler_BaseMap, uv, 0).rgb * _EmissionColor.rgb;
            #endif

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
