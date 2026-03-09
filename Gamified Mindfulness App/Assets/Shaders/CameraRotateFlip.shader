Shader "Hidden/CameraRotateFlip"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            float _Rot90;
            float _FlipX;
            float _FlipY;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float2 RotateUV(float2 uv, float rot90)
            {
                if (rot90 < 0.5) return uv;                              // 0
                if (rot90 < 1.5) return float2(1.0 - uv.y, uv.x);       // 90
                if (rot90 < 2.5) return float2(1.0 - uv.x, 1.0 - uv.y); // 180
                return float2(uv.y, 1.0 - uv.x);                        // 270
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                uv = RotateUV(uv, _Rot90);

                if (_FlipX > 0.5) uv.x = 1.0 - uv.x;
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;

                return tex2D(_MainTex, uv);
            }
            ENDCG
        }
    }

    Fallback Off
}