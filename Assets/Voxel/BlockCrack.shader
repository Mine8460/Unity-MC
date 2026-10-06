Shader "Voxel/BlockCrack"
{
    // Fissures du bloc en train d'être cassé : une surcouche transparente qui assombrit le bloc.
    // Décalée vers la caméra (Offset) pour ne pas clignoter avec les faces du bloc.
    Properties
    {
        _MainTex ("Fissures", 2D) = "white" {}
        _Opacity ("Opacité", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-1"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "Crack"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Offset -1, -1
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Opacity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Pixels nets : lecture au niveau 0, filtrage point (réglé sur la texture)
                half4 tex = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, saturate(IN.uv) * 0.9999, 0);
                return half4(tex.rgb, tex.a * _Opacity);
            }
            ENDHLSL
        }
    }
}
