Shader "Voxel/Sky"
{
    // Ciel façon Minecraft, piloté par DayNightCycle (variables globales _Voxel...) :
    //   - dégradé de l'horizon au zénith, qui suit l'heure ;
    //   - soleil et lune CARRÉS, à bords nets, à leur position exacte (mouvement fluide, même si la lumière
    //     qui fait les ombres avance par crans) ;
    //   - étoiles la nuit.
    // À utiliser comme Skybox (DayNightCycle s'en charge).
    Properties
    {
        _SunSize ("Taille du soleil", Range(0.01, 0.3)) = 0.09
        _MoonSize ("Taille de la lune", Range(0.01, 0.3)) = 0.07
        _SunColor ("Couleur du soleil", Color) = (1, 0.96, 0.78, 1)
        _MoonColor ("Couleur de la lune", Color) = (0.86, 0.9, 1, 1)
        _StarDensity ("Densité d'étoiles", Range(0, 0.01)) = 0.0025
        _StarGrid ("Finesse de la grille d'étoiles", Range(50, 600)) = 260
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _SunSize;
                float _MoonSize;
                half4 _SunColor;
                half4 _MoonColor;
                float _StarDensity;
                float _StarGrid;
            CBUFFER_END

            // Réglés par DayNightCycle à chaque image
            float4 _VoxelSunDir;        // direction du soleil (exacte, sans crans)
            float4 _VoxelSunAxis;       // axe de la course du soleil (oriente les carrés)
            float4 _VoxelZenithColor;
            float4 _VoxelHorizonColor;
            float _VoxelNight;          // 0 = jour, 1 = nuit (étoiles)

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.dir = IN.positionOS.xyz; // la skybox est centrée sur la caméra : la position EST la direction
                return OUT;
            }

            // Un astre carré : vrai si la direction d tombe dans le carré centré sur s, de demi-taille size
            bool InSquare(float3 d, float3 s, float3 axis, float size)
            {
                float facing = dot(d, s);
                if (facing <= 0.0) return false;
                float3 right = normalize(cross(axis, s));
                float3 up = cross(s, right);
                float2 p = float2(dot(d, right), dot(d, up)) / facing; // projection sur le plan de l'astre
                return max(abs(p.x), abs(p.y)) < size;
            }

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 d = normalize(IN.dir);
                float3 sun = normalize(_VoxelSunDir.xyz);
                float3 axis = normalize(_VoxelSunAxis.xyz);

                // Dégradé : horizon -> zénith ; sous l'horizon, un peu plus sombre
                float3 col = lerp(_VoxelHorizonColor.rgb, _VoxelZenithColor.rgb, saturate(d.y * 2.5));
                if (d.y < 0.0) col = lerp(_VoxelHorizonColor.rgb, _VoxelHorizonColor.rgb * 0.55, saturate(-d.y * 4.0));

                // Étoiles : quelques cellules d'une grille de directions, visibles la nuit, au-dessus de l'horizon
                if (_VoxelNight > 0.01 && d.y > 0.0)
                {
                    float3 cell = floor(d * _StarGrid);
                    if (Hash(cell) < _StarDensity)
                        col = lerp(col, float3(0.9, 0.92, 1.0), _VoxelNight * saturate(d.y * 6.0));
                }

                // Soleil et lune (la lune est à l'opposé du soleil)
                if (InSquare(d, sun, axis, _SunSize)) col = _SunColor.rgb;
                else if (InSquare(d, -sun, axis, _MoonSize)) col = lerp(col, _MoonColor.rgb, 0.9);

                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
