// HarfBuzz shapes primary-face ASCII; Unicode, italic, color, explicit kerning
// and missing-glyph runs retain DirectWrite's fallback and layout behavior.
int32_t otr_ascii_shape_available(void) {
#ifdef TOIQTTY_HARFBUZZ
    return 1;
#else
    return 0;
#endif
}
#ifdef TOIQTTY_HARFBUZZ
// Font-owned HarfBuzz storage is overwritten by the next prepare. A prepared
// run is consumed synchronously before that happens; no glyph pointer is
// retained by GPU commands or passed back through the managed boundary.
static int32_t prepareTerminalAscii(OtrTerminalFormat* format,const void* text,uint32_t length,bool utf8,
    const OtrFontFeature* features,uint32_t featureCount) {
    format->asciiPrepared=false;
    if(!format->hbFont || !length || length>4096)return S_FALSE;
    for(uint32_t i=0;i<length;i++) {
        uint32_t value=utf8?static_cast<const uint8_t*>(text)[i]:static_cast<const uint16_t*>(text)[i];
        if(value<32 || value>126)return S_FALSE;
    }
    OTR_TRY {
        auto buffer=format->hbBuffer;hb_buffer_reset(buffer);
        if(utf8)hb_buffer_add_utf8(buffer,static_cast<const char*>(text),static_cast<int>(length),0,static_cast<int>(length));
        else hb_buffer_add_utf16(buffer,static_cast<const uint16_t*>(text),static_cast<int>(length),0,static_cast<int>(length));
        hb_buffer_set_direction(buffer,HB_DIRECTION_LTR);hb_buffer_set_script(buffer,HB_SCRIPT_LATIN);
        hb_buffer_set_language(buffer,format->hbLanguage);
        std::array<hb_feature_t,65> mapped;bool hasKerning=false;
        for(uint32_t i=0;i<featureCount;i++){
            uint32_t tag=features[i].tag;mapped[i]={HB_TAG(tag&255,(tag>>8)&255,(tag>>16)&255,tag>>24),features[i].value,0,~0u};
            if(mapped[i].tag==HB_TAG('k','e','r','n')){if(mapped[i].value)return S_FALSE;hasKerning=true;}
        }
        // IDWriteTextLayout grid spacing leaves pair kerning disabled. HB's
        // automatic kerning can retain second-glyph offsets even after cell
        // advances are corrected (notably Segoe UI's Turkish language system).
        if(!hasKerning)mapped[featureCount++]={HB_TAG('k','e','r','n'),0,0,~0u};
        hb_shape(format->hbFont,buffer,mapped.data(),featureCount);
        if(!hb_buffer_allocation_successful(buffer))throw std::bad_alloc();
        unsigned count=0;const auto* info=hb_buffer_get_glyph_infos(buffer,&count);
        if(!count)return S_FALSE;
        for(unsigned i=0;i<count;i++)if(!info[i].codepoint || info[i].codepoint>65535 || info[i].cluster>=length || (i && info[i].cluster<info[i-1].cluster))return S_FALSE;
        format->asciiLength=length;format->asciiPrepared=true;
    } OTR_CATCH;
}
#endif
int32_t otr_ascii_shape(OtrRenderer* r,OtrTerminalFormat* format,const uint16_t* text,uint32_t length,
    const OtrFontFeature* features,uint32_t featureCount,float cellWidth,OtrTerminalShape** result,uint64_t* ownedBytes) {
    if(!r || !format || !text || !result || !ownedBytes || (featureCount && !features))return E_POINTER;*result=nullptr;*ownedBytes=0;
    if(featureCount>64 || !std::isfinite(cellWidth) || cellWidth<=0)return E_INVALIDARG;
#ifdef TOIQTTY_HARFBUZZ
    if(length<3 || length>4098 || text[0]!=0x202d || text[length-1]!=0x202c)return S_FALSE;
    HRESULT prepared=prepareTerminalAscii(format,text+1,length-2,false,features,featureCount);if(prepared!=S_OK)return prepared;
    format->asciiPrepared=false;
    OTR_TRY {
        unsigned count=0;const auto* info=hb_buffer_get_glyph_infos(format->hbBuffer,&count);const auto* positions=hb_buffer_get_glyph_positions(format->hbBuffer,nullptr);
        auto owned=std::make_unique<OtrTerminalShape>();auto& shape=owned->value;
        GlyphRun run;run.face=format->asciiFace;run.x=0;run.y=format->baseline;run.em=format->asciiEm;
        size_t advancesOffset=(count*sizeof(UINT16)+alignof(float)-1)&~(alignof(float)-1);
        size_t offsetsOffset=advancesOffset+count*sizeof(float);
        auto storage=static_cast<std::byte*>(shape.arena.allocate(offsetsOffset+count*sizeof(DWRITE_GLYPH_OFFSET),alignof(DWRITE_GLYPH_OFFSET)));
        run.glyphs={reinterpret_cast<UINT16*>(storage),count};run.advances={reinterpret_cast<float*>(storage+advancesOffset),count};run.offsets={reinterpret_cast<DWRITE_GLYPH_OFFSET*>(storage+offsetsOffset),count};
        float scale=run.em/static_cast<float>(hb_face_get_upem(hb_font_get_face(format->hbFont)));
        for(unsigned start=0;start<count;) {
            unsigned end=start+1;while(end<count && info[end].cluster==info[start].cluster)end++;
            uint32_t next=end<count?info[end].cluster:length-2;float naturalAdvance=0;
            for(unsigned i=start;i<end;i++){
                run.glyphs[i]=static_cast<UINT16>(info[i].codepoint);run.advances[i]=positions[i].x_advance*scale;
                run.offsets[i]={positions[i].x_offset*scale,positions[i].y_offset*scale};naturalAdvance+=run.advances[i];
            }
            run.advances[end-1]+=static_cast<float>(next-info[start].cluster)*cellWidth-naturalAdvance;start=end;
        }
        shape.runs.push_back(std::move(run));owned->bytes=shape.bytes()+sizeof(OtrTerminalShape)-sizeof(Shape);
        owned->accounting=r->accounting;owned->accounting->shapes.fetch_add(owned->bytes,std::memory_order_relaxed);
        *ownedBytes=owned->bytes;*result=owned.release();
    } OTR_CATCH;
#else
    (void)length;return S_FALSE;
#endif
}
int32_t otr_ascii_prepare_utf8(OtrTerminalFormat* format,const uint8_t* text,uint32_t length,const OtrFontFeature* features,uint32_t featureCount) {
    if(!format || !text || (featureCount && !features))return E_POINTER;
    if(featureCount>64)return E_INVALIDARG;
#ifdef TOIQTTY_HARFBUZZ
    return prepareTerminalAscii(format,text,length,true,features,featureCount);
#else
    (void)length;return S_FALSE;
#endif
}
int32_t otr_ascii_draw_prepared(OtrRenderer* r,OtrTerminalFormat* format,float cellWidth,float x,float y,uint32_t argb) {
    if(!r || !format || !r->active)return E_UNEXPECTED;
    if(!std::isfinite(cellWidth) || cellWidth<=0)return E_INVALIDARG;
#ifdef TOIQTTY_HARFBUZZ
    if(!format->asciiPrepared)return E_UNEXPECTED;
    format->asciiPrepared=false;
    OTR_TRY {
        unsigned count=0;const auto* info=hb_buffer_get_glyph_infos(format->hbBuffer,&count);const auto* positions=hb_buffer_get_glyph_positions(format->hbBuffer,nullptr);
        r->raster->SetDpi(r->current->dpi,r->current->dpi);
        float scale=format->asciiEm/static_cast<float>(hb_face_get_upem(hb_font_get_face(format->hbFont)));
        float dpiScale=r->current->dpi/96,baseline=std::floor((y+format->baseline)*dpiScale+.5f)/dpiScale,pen=x;
        for(unsigned start=0;start<count;) {
            unsigned end=start+1;while(end<count && info[end].cluster==info[start].cluster)end++;
            uint32_t next=end<count?info[end].cluster:format->asciiLength;float naturalAdvance=0;
            for(unsigned i=start;i<end;i++)naturalAdvance+=positions[i].x_advance*scale;
            for(unsigned i=start;i<end;i++) {
                r->glyph(format->asciiFace.Get(),format->asciiEm,static_cast<UINT16>(info[i].codepoint),pen+positions[i].x_offset*scale,baseline-positions[i].y_offset*scale,argb);
                float advance=positions[i].x_advance*scale;
                if(i==end-1)advance+=static_cast<float>(next-info[start].cluster)*cellWidth-naturalAdvance;
                pen+=advance;
            }
            start=end;
        }
    } OTR_CATCH;
#else
    (void)x;(void)y;(void)argb;return E_NOTIMPL;
#endif
}
int32_t otr_ascii_shape_draw(OtrRenderer* r,OtrTerminalShape* shape,float x,float y,uint32_t argb) {
    if(!r || !r->active || !shape)return E_UNEXPECTED;
    OTR_TRY {r->drawShape(shape->value,x,y,argb);}OTR_CATCH;
}
void otr_ascii_shape_release(OtrTerminalShape* shape) {delete shape;}
