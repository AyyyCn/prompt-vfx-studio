Shader "Prompt VFX/Particle Ink"
{
    Properties { _Shape("Shape", Float) = 0 }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Cull Off ZWrite Off Blend SrcAlpha One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Input { float4 position:POSITION; float2 uv:TEXCOORD0; half4 color:COLOR; };
            struct Output { float4 position:SV_POSITION; float2 uv:TEXCOORD0; half4 color:COLOR; };
            CBUFFER_START(UnityPerMaterial)
            float _Shape;
            CBUFFER_END
            Output vert(Input v)
            {
                Output o;
                o.position=TransformObjectToHClip(v.position.xyz);
                o.uv=v.uv;
                o.color=v.color;
                return o;
            }
            half4 frag(Output i):SV_Target
            {
                float2 p=i.uv*2-1;
                float r=length(p);
                float alpha;
                if (_Shape<0.5) alpha=exp(-4*r*r)*saturate(1-r*r); // glow
                else if (_Shape<1.5) { // rim
                    float angle=atan2(p.y,p.x);
                    float d=abs(r-0.79-0.018*sin(angle*7)-0.012*cos(angle*11));
                    alpha=(exp(-d*100)+0.4*exp(-d*20))*saturate((1-r)*8);
                }
                else if (_Shape<2.5) alpha=pow(saturate(1-abs(p.x)*1.8-abs(p.y)),0.7); // shard
                else alpha=exp(-p.x*p.x*36)*saturate(1-abs(p.y)); // streak
                return half4(i.color.rgb,i.color.a*saturate(alpha));
            }
            ENDHLSL
        }
    }
}
