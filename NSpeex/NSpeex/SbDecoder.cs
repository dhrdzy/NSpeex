﻿using System;

namespace NSpeex
{
    /// <summary>
    /// Sideband Speex Decoder
    /// </summary>
    public class SbDecoder:SbCodec,IDecoder
    {
        protected IDecoder lowdec;
        protected Stereo stereo;
        protected bool enhanced;
        private float[] innov2;
        public SbDecoder()
        {
            stereo = new Stereo();
            enhanced = true;
        }
        /// <summary>
        /// Wideband initialisation
        /// </summary>
        public override void wbinit()
        {
            lowdec = new NbDecoder();
            ((NbDecoder)lowdec).nbinit();
            lowdec.PerceptualEnhancement = enhanced;
            base.wbinit();
            init(160, 40, 8, 640, .7f);
        }
        /// <summary>
        /// Ultra-wideband initialisation
        /// </summary>
        public override void uwbinit()
        {
            lowdec = new SbDecoder();
            ((SbDecoder)lowdec).wbinit();
            lowdec.PerceptualEnhancement = enhanced;
            base.uwbinit();
            init(320, 80, 8, 1280, .5f);
        }
        /// <summary>
        /// Initialisation
        /// </summary>
        public override void init(int frameSize, int subframeSize, int lpcSize, int bufSize, float foldingGain)
        {
            base.init(frameSize, subframeSize, lpcSize, bufSize, foldingGain);
            excIdx = 0;
            innov2 = new float[subframeSize];
        }

        /// <summary>
        /// Decode the given input bits.
        /// </summary>
        /// <param name="bits">Speex bits buffer.</param>
        /// <param name="vout">the decoded mono audio frame.</param>
        /// <returns>
        /// 1 if a terminator was found, 0 if not.
        /// </returns>
        public int Decode(Bits bits,float[] vout)
        {
            int i, sub, wideband, ret;
            float[] low_pi_gain, low_exc, low_innov;

            /* Decode the low-band */
            ret = lowdec.Decode(bits, x0d);
            if (ret != 0) 
            {
               return ret;
            }
            bool dtx = lowdec.Dtx;
            if (bits == null) 
            {
              decodeLost(vout, dtx);
              return 0;
            }
            /* Check "wideband bit" */
            wideband = bits.Peek(); 
            if (wideband!=0) {
              /*Regular wideband frame, read the submode*/
              wideband  = bits.UnPack(1);
              submodeID = bits.UnPack(3);
            } 
            else 
            {
              /* was a narrowband frame, set "null submode"*/
              submodeID = 0;
            }
            for (i=0;i<frameSize;i++)
                excBuf[i]=0;
            if (submodes[submodeID] == null) 
            {
                if (dtx)
                {
                    decodeLost(vout, true);
                    return 0;
                 }
                for (i=0;i<frameSize;i++)
                    excBuf[i]=VERY_SMALL;

                first=1;
                 Filters.iir_mem2(excBuf, excIdx, interp_qlpc, high, 0, frameSize,
                       lpcSize, mem_sp);
                filters.fir_mem_up(x0d, Codebook.h0, y0, fullFrameSize, QMF_ORDER, g0_mem);
                filters.fir_mem_up(high, Codebook.h1, y1, fullFrameSize, QMF_ORDER, g1_mem);
                for (i=0;i<fullFrameSize;i++)
                    vout[i]=2*(y0[i]-y1[i]);
                return 0;
            }
            low_pi_gain = lowdec.PitchGain;
            low_exc     = lowdec.Excitation;
            low_innov   = lowdec.Innovation;
            submodes[submodeID].lsqQuant.unquant(qlsp, lpcSize, bits);
            if (first!=0) 
            {
              for (i=0;i<lpcSize;i++)
                old_qlsp[i] = qlsp[i];
            }
            for (sub=0;sub<nbSubframes;sub++)
            {
                float tmp, filter_ratio, el=0.0f, rl=0.0f,rh=0.0f;
                int subIdx=subframeSize*sub;
      
                /* LSP interpolation */
                tmp = (1.0f + sub)/nbSubframes;
                for (i=0;i<lpcSize;i++)
                    interp_qlsp[i] = (1-tmp)*old_qlsp[i] + tmp*qlsp[i];
                Lsp.enforce_margin(interp_qlsp, lpcSize, .05f);
                /* LSPs to x-domain */
                for (i=0;i<lpcSize;i++)
                    interp_qlsp[i] = (float)Math.Cos(interp_qlsp[i]);
                /* LSP to LPC */
                m_lsp.lsp2lpc(interp_qlsp, interp_qlpc, lpcSize);
                if (enhanced) 
                {
                    float k1, k2, k3;
                    k1=submodes[submodeID].lpc_enh_k1;
                    k2=submodes[submodeID].lpc_enh_k2;
                    k3=k1-k2;
                    Filters.bw_lpc(k1, interp_qlpc, awk1, lpcSize);
                    Filters.bw_lpc(k2, interp_qlpc, awk2, lpcSize);
                    Filters.bw_lpc(k3, interp_qlpc, awk3, lpcSize);
                }
                /* Calculate reponse ratio between low & high filter in band middle (4000 Hz) */      
                tmp=1;
                pi_gain[sub]=0;
                for (i=0;i<=lpcSize;i++) 
                {
                    rh += tmp*interp_qlpc[i];
                    tmp = -tmp;
                    pi_gain[sub]+=interp_qlpc[i];
                }
                rl           = low_pi_gain[sub];
                rl           = 1/(Math.Abs(rl)+.01f);
                rh           = 1/(Math.Abs(rh)+.01f);
                filter_ratio = Math.Abs(.01f+rh)/(.01f+Math.Abs(rl));
                /* reset excitation buffer */
                for (i=subIdx;i<subIdx+subframeSize;i++)
                    excBuf[i]=0;
                if (submodes[submodeID].innovation==null)
                {
                    float g;
                    int quant;

                    quant = bits.UnPack(5);
                    g     = (float)Math.Exp(((double)quant-10)/8.0);       
                    g     /= filter_ratio;
        
                    /* High-band excitation using the low-band excitation and a gain */
                    for (i=subIdx;i<subIdx+subframeSize;i++)
                      excBuf[i]=foldingGain*g*low_innov[i];
                }
                else
                {
                    float gc, scale;
                    int qgc = bits.UnPack(4);

                    for (i=subIdx;i<subIdx+subframeSize;i++)
                      el+=low_exc[i]*low_exc[i];

                    gc    = (float)Math.Exp((1/3.7f)*qgc-2);
                    scale = gc*(float)Math.Sqrt(1+el)/filter_ratio;
                    submodes[submodeID].innovation.UnQuant(excBuf, subIdx, subframeSize, bits); 

                    for (i=subIdx;i<subIdx+subframeSize;i++)
                      excBuf[i]*=scale;
                    if (submodes[submodeID].double_codebook!=0) 
                    {
                        for (i=0;i<subframeSize;i++)
                        innov2[i]=0;
                        submodes[submodeID].innovation.UnQuant(innov2, 0, subframeSize, bits); 
                        for (i=0;i<subframeSize;i++)
                            innov2[i]*=scale*(1/2.5f);
                        for (i=0;i<subframeSize;i++)
                            excBuf[subIdx+i] += innov2[i];
                    }
                }
                for (i=subIdx;i<subIdx+subframeSize;i++)
                     high[i]=excBuf[i];
                if (enhanced) 
                {
                    /* Use enhanced LPC filter */
                    Filters.filter_mem2(high, subIdx, awk2, awk1, subframeSize,
                            lpcSize, mem_sp, lpcSize);
                    Filters.filter_mem2(high, subIdx, awk3, interp_qlpc, subframeSize,
                            lpcSize, mem_sp, 0);
                }
                else
                {
                    /* Use regular filter */
                    for (i=0;i<lpcSize;i++)
                        mem_sp[lpcSize+i] = 0;
                    Filters.iir_mem2(high, subIdx, interp_qlpc, high, subIdx,
                                      subframeSize, lpcSize, mem_sp);
                }
            }
            filters.fir_mem_up(x0d, Codebook.h0, y0, fullFrameSize, QMF_ORDER, g0_mem);
            filters.fir_mem_up(high, Codebook.h1, y1, fullFrameSize, QMF_ORDER, g1_mem);

            for (i=0;i<fullFrameSize;i++)
              vout[i]=2*(y0[i]-y1[i]);

            for (i=0;i<lpcSize;i++)
              old_qlsp[i] = qlsp[i];

            first = 0;
            return 0;
        }



        /// <summary>
        /// Decode when packets are lost.
        /// </summary>
        /// <param name="vout">the generated mono audio frame.</param>
        /// <param name="dtx"></param>
        /// <returns>
        ///  0 if successful.
        /// </returns>
        public int decodeLost(float[] vout, bool dtx)
        {
            int i;
            int saved_modeid = 0;

            if (dtx)
            {
                saved_modeid = submodeID;
                submodeID = 1;
            }
            else
            {
                Filters.bw_lpc(0.99f, interp_qlpc, interp_qlpc, lpcSize);
            }
            first = 1;
            awk1 = new float[lpcSize + 1];
            awk2 = new float[lpcSize + 1];
            awk3 = new float[lpcSize + 1];
            if (enhanced)
            {
                float k1, k2, k3;
                if (submodes[submodeID] != null)
                {
                    k1 = submodes[submodeID].lpc_enh_k1;
                    k2 = submodes[submodeID].lpc_enh_k2;
                }
                else
                {
                    k1 = k2 = 0.7f;
                }
                k3 = k1 - k2;
                Filters.bw_lpc(k1, interp_qlpc, awk1, lpcSize);
                Filters.bw_lpc(k2, interp_qlpc, awk2, lpcSize);
                Filters.bw_lpc(k3, interp_qlpc, awk3, lpcSize);
            }
            /* Final signal synthesis from excitation */
            if (!dtx)
            {
                for (i = 0; i < frameSize; i++)
                    excBuf[excIdx + i] *= .9f;
            }
            for (i = 0; i < frameSize; i++)
                high[i] = excBuf[excIdx + i];
            if (enhanced)
            {
                /* Use enhanced LPC filter */
                Filters.filter_mem2(high, 0, awk2, awk1, high, 0, frameSize,
                                    lpcSize, mem_sp, lpcSize);
                Filters.filter_mem2(high, 0, awk3, interp_qlpc, high, 0, frameSize,
                                    lpcSize, mem_sp, 0);
            }
            else
            { /* Use regular filter */
                for (i = 0; i < lpcSize; i++)
                    mem_sp[lpcSize + i] = 0;
                Filters.iir_mem2(high, 0, interp_qlpc, high, 0, frameSize, lpcSize,
                                 mem_sp);
            }
            filters.fir_mem_up(x0d, Codebook.h0, y0, fullFrameSize, QMF_ORDER, g0_mem);
            filters.fir_mem_up(high, Codebook.h1, y1, fullFrameSize, QMF_ORDER, g1_mem);
            for (i=0;i<fullFrameSize;i++)
                vout[i]=2*(y0[i]-y1[i]);
    
            if (dtx) {
              submodeID=saved_modeid;
            }
            return 0;
        }

        /// <summary>
        /// Decode the given bits to stereo.
        /// </summary>
        /// <param name="data">
        /// float array of size 2*frameSize, that contains the mono
        /// audio samples in the first half. When the function has completed, the
        /// array will contain the interlaced stereo audio samples.
        /// </param>
        /// <param name="frameSize">
        /// the size of a frame of mono audio samples.
        /// </param>
        public void DecodeStereo(float[] data,int frameSize)
        {
            stereo.decode(data, frameSize);
        }
       

        public bool PerceptualEnhancement
        {
            get { return enhanced; }
            set { enhanced = value; }
        }


        public bool Dtx
        {
            get { return dtx_enabled != 0; }
        }
    }
}
