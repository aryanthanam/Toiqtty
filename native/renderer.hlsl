// Terminal composition uses premultiplied colors and BGRA atlas/image textures.
struct V { float2 position:POSITION; float2 uv:TEXCOORD; float4 color:COLOR; float shape:SHAPE; };
struct P { float4 position:SV_POSITION; float2 uv:TEXCOORD; float4 color:COLOR; float shape:SHAPE; };
P vs(V input) { P o; o.position=float4(input.position,0,1); o.uv=input.uv; o.color=input.color; o.shape=input.shape; return o; }
Texture2D image:register(t0);
SamplerState sampling:register(s0);
float4 ps(P input):SV_TARGET {
    if(input.shape>0 && input.shape<5) {
        float u=(input.shape==2 || input.shape==4)?1-input.uv.x:input.uv.x;
        float v=abs(input.uv.y*2-1);
        float distance=input.shape>=3?1-sqrt(u*u+v*v):1-u-v;
        return input.color*saturate(distance/max(fwidth(distance),0.00001)+0.5);
    }
    if(input.shape==5) {
        float v=input.uv.y*2-1;
        return input.color*saturate((1-abs(v))/max(fwidth(v),0.00001));
    }
    return image.Sample(sampling,input.uv)*input.color;
}
