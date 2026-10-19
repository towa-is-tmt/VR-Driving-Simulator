// Built-in Render Pipeline 版的後照鏡遮罩 Shader
// 用來取代 Mirror_Mask_Shader.shadergraph (那個是 URP 專屬,在 Built-in 下會變洋紅)
//
// 行為與原本的 Shader Graph 完全相同:
//   BaseColor = _MirrorTexture 取樣結果 (後照鏡相機的 RenderTexture)
//   Alpha     = _MaskTexture 的 Alpha 通道 (決定鏡面形狀)
//
// 屬性名稱刻意與原 Shader Graph 一致 (_MirrorTexture / _MaskTexture),
// 所以直接把材質的 Shader 換成這個,原本指定的貼圖會自動保留。

Shader "Custom/MirrorMask_BuiltIn"
{
    Properties
    {
        _MirrorTexture ("Mirror Texture (RenderTexture)", 2D) = "black" {}
        _MaskTexture   ("Mask Texture (用 Alpha 通道)", 2D) = "white" {}
        _Cutoff        ("Alpha Cutoff (0 = 純混色)", Range(0, 1)) = 0.0

        // 危險警示用染色。白色 (1,1,1,1) = 完全不影響畫面,這是預設值。
        // MirrorHazardAlert 會在危險因子接近時把它改成紅色,鏡面就整片泛紅。
        _TintColor     ("Warning Tint (白色 = 不變色)", Color) = (1, 1, 1, 1)

        // ── 盲點偵測式邊緣警示 (參考真實車輛的 BSM 警示燈) ──
        // 只讓鏡子「邊框」發光,中間影像維持清晰,玩家仍看得見後方來車。
        _WarnColor     ("Warning Edge Color", Color) = (1, 0.12, 0.08, 1)
        _WarnStrength  ("Warning Strength (0 = 關閉)", Range(0, 1)) = 0
        _WarnWidth     ("Warning Edge Width (UV)", Range(0.01, 0.5)) = 0.14
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float2 uvMir  : TEXCOORD0;
                float2 uvMask : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MirrorTexture;
            float4    _MirrorTexture_ST;
            sampler2D _MaskTexture;
            float4    _MaskTexture_ST;
            float     _Cutoff;
            fixed4    _TintColor;
            fixed4    _WarnColor;
            float     _WarnStrength;
            float     _WarnWidth;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                // VR 單通道實例化 (Single Pass Instanced) 必要
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos    = UnityObjectToClipPos(v.vertex);
                o.uvMir  = TRANSFORM_TEX(v.uv, _MirrorTexture);
                o.uvMask = TRANSFORM_TEX(v.uv, _MaskTexture);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                fixed4 mirror = tex2D(_MirrorTexture, i.uvMir);
                fixed  mask   = tex2D(_MaskTexture, i.uvMask).a;

                // _Cutoff 為 0 時不裁切,行為等同原本的純 Alpha 混色
                clip(mask - _Cutoff);

                fixed3 rgb = mirror.rgb * _TintColor.rgb;

                // 邊緣警示:算出這個像素離鏡子四邊最近的距離,越靠邊越亮。
                // _WarnStrength 為 0 時整段沒有任何影響,鏡子完全維持原樣。
                float2 dEdge = min(i.uvMask, 1.0 - i.uvMask);
                float  edge  = 1.0 - smoothstep(0.0, _WarnWidth, min(dEdge.x, dEdge.y));
                rgb = lerp(rgb, _WarnColor.rgb, saturate(edge * _WarnStrength));

                return fixed4(rgb, mask);
            }
            ENDCG
        }
    }

    FallBack Off
}
