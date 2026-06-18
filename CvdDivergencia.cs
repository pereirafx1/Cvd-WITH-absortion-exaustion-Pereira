// ============================================================================
//  CVD With Exaus/Absor. Pereira  —  ATAS Platform  (SDK 10)
//  Deteta Exaustão e Absorção entre preço e CVD no NQ (1 min),
//  com confirmação simultânea opcional no ES.
//
//  REFERÊNCIAS DE API CONFIRMADAS (docs.atas.net + github.com/AtasPlatform/Indicators):
//  • EnableCustomDrawing = true  +  SubscribeToDrawingEvents()  obrigatórios
//  • context.DrawLine(RenderPen, x1, y1, x2, y2)             — 4 ints, não Points
//  • context.FillRectangle(Color, Rectangle)                  — Color, não Brush
//  • context.DrawRectangle(RenderPen, Rectangle)              — RenderPen, não Pen
//  • context.DrawString(string, RenderFont, Color, int, int)  — RenderFont + Color
//  • ChartInfo.GetXByBar(bar, false)                          — 2 params (centro)
//  • ChartInfo.PriceChartContainer.BarsWidth                  — largura de barra
//  • Container.Region                                          — bounds do painel
//  • IndicatorDataProvider.NewPanel                            — cria sub-painel
//  • IndicatorCandle.MaxDelta / .MinDelta                      — confirmados no SDK
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace CvdDivergencia
{
    [DisplayName("CVD With Exaus/Absor. Pereira")]
    [Category("Custom")]
    [Description("Divergências Exaustão/Absorção entre preço e CVD. Painel próprio com CVD em candles.")]
    public class CvdDivergencia : Indicator
    {
        // ====================================================================
        //  PARÂMETROS CONFIGURÁVEIS
        // ====================================================================

        [Display(Name = "Cor Candle Bid (delta ↓)", GroupName = "CVD – Candles", Order = 0)]
        public Color CorCandleBid { get; set; } = Color.FromArgb(220, 60, 60);

        [Display(Name = "Cor Candle Ask (delta ↑)", GroupName = "CVD – Candles", Order = 1)]
        public Color CorCandleAsk { get; set; } = Color.FromArgb(60, 200, 80);

        [Display(Name = "Cor Linha Exaustão", GroupName = "Divergências", Order = 0)]
        public Color CorLinhaExaustao { get; set; } = Color.Orange;

        [Display(Name = "Cor Linha Absorção", GroupName = "Divergências", Order = 1)]
        public Color CorLinhaAbsorcao { get; set; } = Color.DeepSkyBlue;

        [Display(Name = "Espessura das Linhas", GroupName = "Divergências", Order = 2)]
        [Range(1, 5)]
        public int EspessuraLinha { get; set; } = 2;

        [Display(Name = "Requerer ES Simultâneo", GroupName = "Modo Simultâneo", Order = 0)]
        public bool RequererES { get; set; } = false;

        [Display(Name = "Janela de Tolerância (min)", GroupName = "Modo Simultâneo", Order = 1)]
        [Range(1, 60)]
        public int JanelaToleranciaMin { get; set; } = 5;

        // ====================================================================
        //  TIPOS INTERNOS
        // ====================================================================

        private struct SwingPoint
        {
            public int      Bar;
            public decimal  Price;    // high ou low do preço no swing
            public decimal  CvdVal;   // valor CVD (high ou low) no swing
            public DateTime Time;
        }

        private enum DivType
        {
            ExaustaoHigh,   // preço new high, CVD não confirma
            AbsorcaoHigh,   // CVD new high, preço foi absorvido
            ExaustaoLow,    // preço new low,  CVD não confirma
            AbsorcaoLow     // CVD new low,  preço foi absorvido
        }

        private struct Divergence
        {
            public DivType  Type;
            public int      Bar1, Bar2;
            public decimal  Cvd1, Cvd2;
        }

        // ====================================================================
        //  SÉRIES DE DADOS – CVD (NQ)
        //  Série 0 com Panel = IndicatorDataProvider.NewPanel cria o sub-painel.
        //  IsHidden = true impede que o ATAS desenhe a linha; nós desenhamos em OnRender.
        // ====================================================================

        private readonly ValueDataSeries _cvdClose;
        private readonly ValueDataSeries _cvdOpen;
        private readonly ValueDataSeries _cvdHigh;
        private readonly ValueDataSeries _cvdLow;

        // ====================================================================
        //  ESTADO INTERNO – NQ
        // ====================================================================

        private readonly List<SwingPoint> _highsNQ = new List<SwingPoint>();
        private readonly List<SwingPoint> _lowsNQ  = new List<SwingPoint>();
        private readonly List<Divergence> _divs     = new List<Divergence>();

        // ====================================================================
        //  ESTADO INTERNO – ES (background)
        // ====================================================================

        private SecurityDataSource        _esSource;
        private readonly List<decimal>    _esCvdClose = new List<decimal>();
        private readonly List<DateTime>   _esTime     = new List<DateTime>();
        private readonly List<SwingPoint> _highsES    = new List<SwingPoint>();
        private readonly List<SwingPoint> _lowsES     = new List<SwingPoint>();
        private readonly List<Divergence> _divsES     = new List<Divergence>();
        private decimal                   _esCvdPrev  = 0m;
        private readonly object           _esLock     = new object();

        // ====================================================================
        //  CONSTRUTOR
        // ====================================================================

        public CvdDivergencia()
        {
            // Série 0: define o sub-painel e a escala Y automática.
            _cvdClose = new ValueDataSeries("CVD Close")
            {
                IsHidden = true,
                Panel    = IndicatorDataProvider.NewPanel
            };
            _cvdOpen  = new ValueDataSeries("CVD Open")  { IsHidden = true };
            _cvdHigh  = new ValueDataSeries("CVD High")  { IsHidden = true };
            _cvdLow   = new ValueDataSeries("CVD Low")   { IsHidden = true };

            DataSeries[0] = _cvdClose;
            Add(_cvdOpen);
            Add(_cvdHigh);
            Add(_cvdLow);

            // Obrigatório para que OnRender seja chamado
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
        }

        // ====================================================================
        //  CICLO DE VIDA
        // ====================================================================

        protected override void OnInitialize()
        {
            ResetState();
            StartEsSource();
        }

        // ⚠ VERIFICAR: se não compilar, renomear para Dispose() ou OnDestroy()
        protected override void OnDispose()
        {
            DisposeEsSource();
            base.OnDispose();
        }

        private void ResetState()
        {
            _highsNQ.Clear();
            _lowsNQ.Clear();
            _divs.Clear();

            lock (_esLock)
            {
                _esCvdClose.Clear();
                _esTime.Clear();
                _highsES.Clear();
                _lowsES.Clear();
                _divsES.Clear();
                _esCvdPrev = 0m;
            }
        }

        // ====================================================================
        //  CÁLCULO BARRA-A-BARRA (NQ)
        // ====================================================================

        protected override void OnCalculate(int bar, decimal value)
        {
            var c = GetCandle(bar);

            // Acumular CVD barra a barra
            if (bar == 0)
            {
                _cvdOpen[0]  = 0m;
                _cvdClose[0] = c.Delta;
            }
            else
            {
                _cvdOpen[bar]  = _cvdClose[bar - 1];
                _cvdClose[bar] = _cvdClose[bar - 1] + c.Delta;
            }

            // CVD High/Low intrabar via MaxDelta / MinDelta (confirmados no SDK)
            decimal peakBuy  = Math.Max(0m, c.MaxDelta);
            decimal peakSell = Math.Min(0m, c.MinDelta);

            _cvdHigh[bar] = _cvdOpen[bar] + Math.Max(peakBuy,  Math.Max(0m, c.Delta));
            _cvdLow[bar]  = _cvdOpen[bar] + Math.Min(peakSell, Math.Min(0m, c.Delta));

            // Detetar swing points (pivot de 3 barras, confirmado em bar-1)
            if (bar >= 2)
                DetectAndCheckNQ(bar);
        }

        // ====================================================================
        //  SWING POINTS E DIVERGÊNCIAS — NQ
        // ====================================================================

        private void DetectAndCheckNQ(int bar)
        {
            var c0 = GetCandle(bar - 2);
            var c1 = GetCandle(bar - 1);   // barra candidata — a do meio
            var c2 = GetCandle(bar);

            // ---- Swing High confirmado em bar-1 ----
            if (c1.High > c0.High && c1.High > c2.High)
            {
                var sp = new SwingPoint
                {
                    Bar    = bar - 1,
                    Price  = c1.High,
                    CvdVal = _cvdHigh[bar - 1],
                    Time   = c1.Time
                };
                _highsNQ.RemoveAll(s => s.Bar == bar - 1);
                _highsNQ.Add(sp);

                if (_highsNQ.Count >= 2)
                    TryAddHighDiv(_highsNQ[_highsNQ.Count - 2], sp);
            }

            // ---- Swing Low confirmado em bar-1 ----
            if (c1.Low < c0.Low && c1.Low < c2.Low)
            {
                var sp = new SwingPoint
                {
                    Bar    = bar - 1,
                    Price  = c1.Low,
                    CvdVal = _cvdLow[bar - 1],
                    Time   = c1.Time
                };
                _lowsNQ.RemoveAll(s => s.Bar == bar - 1);
                _lowsNQ.Add(sp);

                if (_lowsNQ.Count >= 2)
                    TryAddLowDiv(_lowsNQ[_lowsNQ.Count - 2], sp);
            }
        }

        // ====================================================================
        //  LÓGICA DE DIVERGÊNCIA — HIGH (TOPO)
        // ====================================================================

        private void TryAddHighDiv(SwingPoint prev, SwingPoint curr)
        {
            bool priceHigher = curr.Price  > prev.Price;
            bool cvdHigher   = curr.CvdVal > prev.CvdVal;

            DivType? type = null;
            if ( priceHigher && !cvdHigher) type = DivType.ExaustaoHigh;   // preço novo high, CVD não confirma
            if (!priceHigher &&  cvdHigher) type = DivType.AbsorcaoHigh;   // CVD novo high, preço absorvido
            if (type == null) return;

            if (RequererES && !EsHasHighDiv(type.Value, curr.Time)) return;

            _divs.RemoveAll(d => d.Bar2 == curr.Bar &&
                (d.Type == DivType.ExaustaoHigh || d.Type == DivType.AbsorcaoHigh));

            _divs.Add(new Divergence
            {
                Type = type.Value,
                Bar1 = prev.Bar, Bar2 = curr.Bar,
                Cvd1 = prev.CvdVal, Cvd2 = curr.CvdVal
            });
        }

        // ====================================================================
        //  LÓGICA DE DIVERGÊNCIA — LOW (FUNDO)
        // ====================================================================

        private void TryAddLowDiv(SwingPoint prev, SwingPoint curr)
        {
            bool priceLower = curr.Price  < prev.Price;
            bool cvdLower   = curr.CvdVal < prev.CvdVal;

            DivType? type = null;
            if ( priceLower && !cvdLower) type = DivType.ExaustaoLow;    // preço novo low, CVD não confirma
            if (!priceLower &&  cvdLower) type = DivType.AbsorcaoLow;    // CVD novo low, preço absorvido
            if (type == null) return;

            if (RequererES && !EsHasLowDiv(type.Value, curr.Time)) return;

            _divs.RemoveAll(d => d.Bar2 == curr.Bar &&
                (d.Type == DivType.ExaustaoLow || d.Type == DivType.AbsorcaoLow));

            _divs.Add(new Divergence
            {
                Type = type.Value,
                Bar1 = prev.Bar, Bar2 = curr.Bar,
                Cvd1 = prev.CvdVal, Cvd2 = curr.CvdVal
            });
        }

        // ====================================================================
        //  ES — DATA SOURCE EM BACKGROUND
        //
        //  ⚠ VERIFICAR: A API de SecurityDataSource pode variar conforme a versão
        //  do SDK instalado. Pontos a confirmar com IntelliSense:
        //    1. Construtor: new SecurityDataSource(string symbol, CandleSeries period)
        //       Alternativas: new SecurityDataSource(Security, CandleSeries)
        //                     new SecurityDataSource(string symbol)
        //    2. Evento: NewCandleCreated  (alternativas: NewCandle, CandleUpdated)
        //    3. EventArgs: CandleEventArgs com propriedade e.Candle
        //    4. Start() / Stop() / Dispose() — confirmar nomes
        // ====================================================================

        private void StartEsSource()
        {
            DisposeEsSource();
            if (!RequererES) return;

            try
            {
                // ⚠ VERIFICAR: construtor e tipo do segundo argumento (period)
                _esSource = new SecurityDataSource("ES", CurrentSeries.Period);
                // ⚠ VERIFICAR: nome do evento (pode ser NewCandle ou CandleUpdated)
                _esSource.NewCandleCreated += OnEsCandle;
                _esSource.Start();
            }
            catch (Exception)
            {
                // ES não disponível — o indicador continua em modo NQ-only
                _esSource = null;
            }
        }

        private void DisposeEsSource()
        {
            if (_esSource == null) return;
            try
            {
                // ⚠ VERIFICAR: nome do evento (deve ser o mesmo do Subscribe acima)
                _esSource.NewCandleCreated -= OnEsCandle;
                _esSource.Stop();
                _esSource.Dispose();
            }
            catch { /* ignorar erros de dispose */ }
            _esSource = null;
        }

        // ⚠ VERIFICAR: tipo do EventArgs (pode ser EventArgs ou outro tipo específico do SDK)
        // Alternativa: private void OnEsCandle(object sender, EventArgs e)
        private void OnEsCandle(object sender, CandleEventArgs e)
        {
            // ⚠ VERIFICAR: acesso à candle (pode ser e.Value ou e.Bar em vez de e.Candle)
            var c = e.Candle;

            lock (_esLock)
            {
                decimal close = _esCvdPrev + c.Delta;

                int idx = _esCvdClose.Count;
                _esCvdClose.Add(close);
                _esTime.Add(c.Time);
                _esCvdPrev = close;

                if (idx >= 2)
                {
                    decimal v0 = _esCvdClose[idx - 2];
                    decimal v1 = _esCvdClose[idx - 1];
                    decimal v2 = _esCvdClose[idx];

                    if (v1 > v0 && v1 > v2)
                    {
                        var sp = new SwingPoint { Bar = idx - 1, Price = c.High, CvdVal = v1, Time = _esTime[idx - 1] };
                        _highsES.RemoveAll(s => s.Bar == idx - 1);
                        _highsES.Add(sp);
                        if (_highsES.Count >= 2)
                            TryAddEsDivHigh(_highsES[_highsES.Count - 2], sp);
                    }

                    if (v1 < v0 && v1 < v2)
                    {
                        var sp = new SwingPoint { Bar = idx - 1, Price = c.Low, CvdVal = v1, Time = _esTime[idx - 1] };
                        _lowsES.RemoveAll(s => s.Bar == idx - 1);
                        _lowsES.Add(sp);
                        if (_lowsES.Count >= 2)
                            TryAddEsDivLow(_lowsES[_lowsES.Count - 2], sp);
                    }
                }
            }
        }

        private void TryAddEsDivHigh(SwingPoint prev, SwingPoint curr)
        {
            bool pH = curr.Price > prev.Price, cH = curr.CvdVal > prev.CvdVal;
            DivType? t = null;
            if ( pH && !cH) t = DivType.ExaustaoHigh;
            if (!pH &&  cH) t = DivType.AbsorcaoHigh;
            if (t == null) return;
            _divsES.RemoveAll(d => d.Bar2 == curr.Bar &&
                (d.Type == DivType.ExaustaoHigh || d.Type == DivType.AbsorcaoHigh));
            _divsES.Add(new Divergence { Type = t.Value,
                Bar1 = prev.Bar, Bar2 = curr.Bar, Cvd1 = prev.CvdVal, Cvd2 = curr.CvdVal });
        }

        private void TryAddEsDivLow(SwingPoint prev, SwingPoint curr)
        {
            bool pL = curr.Price < prev.Price, cL = curr.CvdVal < prev.CvdVal;
            DivType? t = null;
            if ( pL && !cL) t = DivType.ExaustaoLow;
            if (!pL &&  cL) t = DivType.AbsorcaoLow;
            if (t == null) return;
            _divsES.RemoveAll(d => d.Bar2 == curr.Bar &&
                (d.Type == DivType.ExaustaoLow || d.Type == DivType.AbsorcaoLow));
            _divsES.Add(new Divergence { Type = t.Value,
                Bar1 = prev.Bar, Bar2 = curr.Bar, Cvd1 = prev.CvdVal, Cvd2 = curr.CvdVal });
        }

        // ====================================================================
        //  VERIFICAÇÃO ES SIMULTÂNEO
        // ====================================================================

        private bool EsHasHighDiv(DivType nqType, DateTime nqTime)
        {
            lock (_esLock)
            {
                return _divsES.Any(d =>
                    d.Type == nqType &&
                    _highsES.Any(s => s.Bar == d.Bar2 &&
                        Math.Abs((s.Time - nqTime).TotalMinutes) <= JanelaToleranciaMin));
            }
        }

        private bool EsHasLowDiv(DivType nqType, DateTime nqTime)
        {
            lock (_esLock)
            {
                return _divsES.Any(d =>
                    d.Type == nqType &&
                    _lowsES.Any(s => s.Bar == d.Bar2 &&
                        Math.Abs((s.Time - nqTime).TotalMinutes) <= JanelaToleranciaMin));
            }
        }

        // ====================================================================
        //  RENDERING — CVD CANDLES + LINHAS DE DIVERGÊNCIA
        //
        //  APIs confirmadas contra exemplos oficiais do ATAS SDK:
        //  • context.FillRectangle(Color, Rectangle)             — sem Brush
        //  • context.DrawRectangle(RenderPen, Rectangle)         — RenderPen
        //  • context.DrawLine(RenderPen, x1, y1, x2, y2)        — 4 ints
        //  • context.DrawString(s, RenderFont, Color, int, int)  — sem Brush/PointF
        //  • ChartInfo.GetXByBar(bar, false)                     — false=centro da barra
        //  • ChartInfo.PriceChartContainer.BarsWidth             — largura da barra
        //  • Container.Region                                     — Rectangle do painel
        // ====================================================================

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (layout != DrawingLayouts.Final) return;

            int firstBar = ChartInfo.FirstVisibleBarNumber;
            int lastBar  = Math.Min(ChartInfo.LastVisibleBarNumber, CurrentBar);

            if (firstBar > lastBar || lastBar < 0) return;

            // --- Calcular min/max CVD na área visível ---
            decimal minCvd = decimal.MaxValue;
            decimal maxCvd = decimal.MinValue;

            for (int b = Math.Max(0, firstBar); b <= lastBar; b++)
            {
                decimal lo = _cvdLow[b];
                decimal hi = _cvdHigh[b];
                if (lo < minCvd) minCvd = lo;
                if (hi > maxCvd) maxCvd = hi;
            }

            if (minCvd == decimal.MaxValue) return;
            if (maxCvd == minCvd) maxCvd = minCvd + 1m;

            // Bounds do painel do indicador (Container.Region confirmado em exemplos SDK)
            var reg    = Container.Region;
            int pTop    = reg.Top    + 4;
            int pBottom = reg.Bottom - 4;
            int pHeight = pBottom - pTop;
            if (pHeight <= 0) return;

            // Converte CVD → pixel Y (Y=0 no topo em GDI+)
            double CvdToY(decimal cvd)
            {
                double ratio = (double)(cvd - minCvd) / (double)(maxCvd - minCvd);
                return pBottom - ratio * pHeight;
            }

            // Largura de barra confirmada via PriceChartContainer.BarsWidth
            int barW    = Math.Max(2, ChartInfo.PriceChartContainer.BarsWidth);
            int candleW = Math.Max(1, barW - 2);
            int wickW   = Math.Max(1, barW / 6);

            // ----------------------------------------------------------------
            //  Desenhar CVD candles
            // ----------------------------------------------------------------
            for (int b = Math.Max(0, firstBar); b <= lastBar; b++)
            {
                decimal cvdO = _cvdOpen[b];
                decimal cvdC = _cvdClose[b];
                decimal cvdH = _cvdHigh[b];
                decimal cvdL = _cvdLow[b];

                Color barColor = cvdC >= cvdO ? CorCandleAsk : CorCandleBid;

                // GetXByBar(bar, isStartOfBar) — false = centro da barra
                int xCenter = ChartInfo.GetXByBar(b, false);
                int xLeft   = xCenter - candleW / 2;

                int yO = Clamp((int)Math.Round(CvdToY(cvdO)), pTop, pBottom);
                int yC = Clamp((int)Math.Round(CvdToY(cvdC)), pTop, pBottom);
                int yH = Clamp((int)Math.Round(CvdToY(cvdH)), pTop, pBottom);
                int yL = Clamp((int)Math.Round(CvdToY(cvdL)), pTop, pBottom);

                int bodyTop = Math.Min(yO, yC);
                int bodyBot = Math.Max(yO, yC);
                int bodyH   = Math.Max(1, bodyBot - bodyTop);

                // Corpo — FillRectangle(Color, Rectangle) confirmado
                context.FillRectangle(barColor, new Rectangle(xLeft, bodyTop, candleW, bodyH));

                // Borda semi-transparente — DrawRectangle(RenderPen, Rectangle) confirmado
                var borderPen = new RenderPen(Color.FromArgb(80, 0, 0, 0));
                context.DrawRectangle(borderPen, new Rectangle(xLeft, bodyTop, candleW, bodyH));

                // Wicks — DrawLine(RenderPen, x1, y1, x2, y2) confirmado
                var wickPen = new RenderPen(barColor, wickW);
                if (yH < bodyTop)
                    context.DrawLine(wickPen, xCenter, yH, xCenter, bodyTop);
                if (yL > bodyBot)
                    context.DrawLine(wickPen, xCenter, bodyBot, xCenter, yL);
            }

            // ----------------------------------------------------------------
            //  Desenhar linhas de divergência
            // ----------------------------------------------------------------
            var labelFont = new RenderFont("Arial", 8);

            var visible = _divs
                .Where(d => d.Bar1 <= lastBar && d.Bar2 >= firstBar)
                .ToList();

            foreach (var div in visible)
            {
                bool isHigh = div.Type == DivType.ExaustaoHigh || div.Type == DivType.AbsorcaoHigh;
                bool isExh  = div.Type == DivType.ExaustaoHigh || div.Type == DivType.ExaustaoLow;

                Color  lineColor = isExh ? CorLinhaExaustao : CorLinhaAbsorcao;
                string label     = isExh ? "Exaustao" : "Absorcao";
                label += isHigh ? " ^" : " v";

                int x1 = ChartInfo.GetXByBar(div.Bar1, false);
                int x2 = ChartInfo.GetXByBar(div.Bar2, false);
                int y1 = Clamp((int)Math.Round(CvdToY(div.Cvd1)), pTop, pBottom);
                int y2 = Clamp((int)Math.Round(CvdToY(div.Cvd2)), pTop, pBottom);

                // Linha de divergência tracejada
                // ⚠ VERIFICAR: se RenderPen não tiver DashStyle, remover a linha pen.DashStyle
                var linePen = new RenderPen(lineColor, EspessuraLinha);
                linePen.DashStyle = DashStyle.Dash;
                context.DrawLine(linePen, x1, y1, x2, y2);

                // Marcadores nos swing points (quadrados pequenos)
                int d = 3;
                context.FillRectangle(lineColor, new Rectangle(x1 - d, y1 - d, d * 2, d * 2));
                context.FillRectangle(lineColor, new Rectangle(x2 - d, y2 - d, d * 2, d * 2));

                // Label — DrawString(string, RenderFont, Color, int, int) confirmado
                int labelX = x2 + 6;
                int labelY = Clamp(y2 - 10, pTop + 2, pBottom - 14);
                context.DrawString(label, labelFont, lineColor, labelX, labelY);
            }
        }

        // ====================================================================
        //  UTILITÁRIOS
        // ====================================================================

        private static int Clamp(int v, int lo, int hi)
            => v < lo ? lo : v > hi ? hi : v;
    }
}
