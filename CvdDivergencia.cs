// ============================================================================
//  CVD Divergência NQ/ES  —  ATAS Platform  (SDK 10)
//  Deteta Exaustão e Absorção entre preço e CVD no NQ (1 min),
//  com confirmação simultânea opcional no ES.
//
//  NOTAS DE COMPILAÇÃO:
//  Alguns nomes de API do ATAS SDK 10 podem diferir ligeiramente entre versões.
//  Os pontos assinalados com "⚠ SDK" podem precisar de ajuste.
//  Consultar o IntelliSense ou os exemplos em %PROGRAMFILES(X86)%\ATAS Platform\Examples
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace CvdDivergencia
{
    [DisplayName("CVD Divergência NQ/ES")]
    [Category("Custom")]
    [Description("Divergências Exaustão/Absorção entre preço e CVD. Painel próprio com CVD em candles.")]
    public class CvdDivergencia : Indicator
    {
        // ====================================================================
        //  PARÂMETROS CONFIGURÁVEIS  (visíveis no painel de settings do ATAS)
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
            public int     Bar;
            public decimal Price;    // high ou low do preço no swing
            public decimal CvdVal;   // valor CVD (high ou low) no swing
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
            public int      Bar1, Bar2;   // barras dos dois swing points
            public decimal  Cvd1, Cvd2;  // valores CVD nessas barras
        }

        // ====================================================================
        //  SÉRIES DE DADOS – CVD (NQ)
        //
        //  ⚠ SDK: 'IndicatorDataProvider.NewPanel' cria o sub-painel separado.
        //  Se não compilar, substituir por: Panel = "CVD"  ou  Panel = 1
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
            // Série 0: define o sub-painel e a escala Y automática do ATAS.
            // IsHidden = true → o ATAS não desenha a linha por si; nós desenhamos as candles em OnRender.
            _cvdClose = new ValueDataSeries("CVD Close")
            {
                IsHidden = true,
                Panel    = IndicatorDataProvider.NewPanel   // ⚠ SDK: cria painel separado
            };
            _cvdOpen  = new ValueDataSeries("CVD Open")  { IsHidden = true };
            _cvdHigh  = new ValueDataSeries("CVD High")  { IsHidden = true };
            _cvdLow   = new ValueDataSeries("CVD Low")   { IsHidden = true };

            DataSeries[0] = _cvdClose;
            Add(_cvdOpen);
            Add(_cvdHigh);
            Add(_cvdLow);
        }

        // ====================================================================
        //  CICLO DE VIDA
        // ====================================================================

        protected override void OnInitialize()
        {
            ResetState();
            StartEsSource();
        }

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

            // --- Acumular CVD barra a barra ---
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

            // CVD High/Low intrabar:
            // MaxDelta = pico comprador acumulado durante a barra
            // MinDelta = pico vendedor acumulado durante a barra
            // Se c.MaxDelta / c.MinDelta não existirem no SDK, usar Open/Close (comentar as duas linhas abaixo).
            decimal peakBuy  = Math.Max(0m, c.MaxDelta);
            decimal peakSell = Math.Min(0m, c.MinDelta);

            _cvdHigh[bar] = _cvdOpen[bar] + Math.Max(peakBuy,  Math.Max(0m, c.Delta));
            _cvdLow[bar]  = _cvdOpen[bar] + Math.Min(peakSell, Math.Min(0m, c.Delta));

            // --- Swing point detection (pivot de 3 barras, confirmado em bar-1) ---
            if (bar >= 2)
                DetectAndCheckNQ(bar);
        }

        // ====================================================================
        //  DETECÇÃO DE SWING POINTS E DIVERGÊNCIAS — NQ
        // ====================================================================

        private void DetectAndCheckNQ(int bar)
        {
            var c0 = GetCandle(bar - 2);
            var c1 = GetCandle(bar - 1);   // candidato a swing — barra do meio
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
            if ( priceHigher && !cvdHigher) type = DivType.ExaustaoHigh;  // preço novo high, CVD não acompanha
            if (!priceHigher &&  cvdHigher) type = DivType.AbsorcaoHigh;  // CVD novo high, preço absorvido
            if (type == null) return;

            // Filtro modo simultâneo ES
            if (RequererES && !EsHasHighDiv(type.Value, curr.Time)) return;

            // Substituir divergência existente no mesmo swing
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
            if ( priceLower && !cvdLower) type = DivType.ExaustaoLow;   // preço novo low, CVD não acompanha
            if (!priceLower &&  cvdLower) type = DivType.AbsorcaoLow;   // CVD novo low, preço absorvido
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
        // ====================================================================

        private void StartEsSource()
        {
            DisposeEsSource();
            if (!RequererES) return;

            try
            {
                // ⚠ SDK: SecurityDataSource carrega barras de outro instrumento.
                // Primeiro arg: símbolo (ajustar para contrato activo: "ESM25", "ESU25", etc.)
                // Segundo arg: período — idêntico ao gráfico NQ (CandleSeries de 1 min).
                // API exacta: ver ATAS.Indicators.SecurityDataSource no SDK instalado.
                //
                // Alternativas possíveis:
                //   new SecurityDataSource("ES")
                //   new SecurityDataSource("ES", new CandleSeries(TimeFrameType.Minute, 1))
                _esSource = new SecurityDataSource("ES", CurrentSeries.Period);
                _esSource.NewCandleCreated += OnEsCandle;
                _esSource.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CvdDiv] ES DataSource indisponível: {ex.Message}");
                _esSource = null;
            }
        }

        private void DisposeEsSource()
        {
            if (_esSource == null) return;
            try
            {
                _esSource.NewCandleCreated -= OnEsCandle;
                _esSource.Stop();
                _esSource.Dispose();
            }
            catch { /* ignorar erros de dispose */ }
            _esSource = null;
        }

        // ⚠ SDK: assinatura do handler pode variar — ajustar EventArgs se necessário
        private void OnEsCandle(object sender, CandleEventArgs e)
        {
            var c = e.Candle;

            lock (_esLock)
            {
                decimal close = _esCvdPrev + c.Delta;

                // Manter histórico de CVD close e timestamps do ES
                int idx = _esCvdClose.Count;
                _esCvdClose.Add(close);
                _esTime.Add(c.Time);
                _esCvdPrev = close;

                // Swing points do ES (usando CVD close como proxy de high/low)
                if (idx >= 2)
                {
                    decimal v0 = _esCvdClose[idx - 2];
                    decimal v1 = _esCvdClose[idx - 1];   // candidato
                    decimal v2 = _esCvdClose[idx];

                    if (v1 > v0 && v1 > v2)  // ES Swing High (CVD)
                    {
                        var sp = new SwingPoint
                        {
                            Bar    = idx - 1,
                            Price  = c.High,           // preço ES
                            CvdVal = v1,
                            Time   = _esTime[idx - 1]
                        };
                        _highsES.RemoveAll(s => s.Bar == idx - 1);
                        _highsES.Add(sp);
                        if (_highsES.Count >= 2)
                            TryAddEsDivHigh(_highsES[_highsES.Count - 2], sp);
                    }

                    if (v1 < v0 && v1 < v2)  // ES Swing Low (CVD)
                    {
                        var sp = new SwingPoint
                        {
                            Bar    = idx - 1,
                            Price  = c.Low,
                            CvdVal = v1,
                            Time   = _esTime[idx - 1]
                        };
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
            bool pH = curr.Price  > prev.Price;
            bool cH = curr.CvdVal > prev.CvdVal;
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
            bool pL = curr.Price  < prev.Price;
            bool cL = curr.CvdVal < prev.CvdVal;
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
        //  ⚠ SDK: 'Container.Region' é a propriedade standard em ATAS SDK 10
        //  para obter o Rectangle do painel do indicador.
        //  Alternativas se não compilar:
        //    ChartInfo.PanelsBoundsInfo[DataSeries[0].Panel].Bounds
        //    ou inspecionar ChartInfo no IntelliSense
        // ====================================================================

        public override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (layout != DrawingLayouts.Final) return;

            int firstBar = ChartInfo.FirstVisibleBarNumber;
            int lastBar  = Math.Min(ChartInfo.LastVisibleBarNumber, CurrentBar);

            if (firstBar > lastBar || lastBar < 0) return;

            // --- Calcular min/max CVD na área visível para escala Y ---
            decimal minCvd = decimal.MaxValue;
            decimal maxCvd = decimal.MinValue;

            for (int b = Math.Max(0, firstBar); b <= lastBar; b++)
            {
                decimal lo = _cvdLow[b];
                decimal hi = _cvdHigh[b];
                if (lo < minCvd) minCvd = lo;
                if (hi > maxCvd) maxCvd = hi;
            }

            if (minCvd == decimal.MaxValue) return;   // sem dados ainda
            if (maxCvd == minCvd) { maxCvd = minCvd + 1m; }

            // Obter bounds do painel do indicador
            // ⚠ SDK: se Container.Region não existir, ver nota no cabeçalho desta secção
            var panelRect = Container.Region;
            int pTop    = panelRect.Top    + 4;    // margem de 4 px no topo
            int pBottom = panelRect.Bottom - 4;    // margem de 4 px na base
            int pHeight = pBottom - pTop;

            if (pHeight <= 0) return;

            // Converte valor CVD para pixel Y (GDI+: Y=0 no topo, cresce para baixo)
            double CvdToY(decimal cvd)
            {
                double ratio = (double)(cvd - minCvd) / (double)(maxCvd - minCvd);
                return pBottom - ratio * pHeight;
            }

            // Largura de barra (em pixeis) — usada para sizing das candles
            int xBar0   = ChartInfo.GetXByBar(firstBar);
            int xBar1   = firstBar + 1 <= lastBar ? ChartInfo.GetXByBar(firstBar + 1) : xBar0 + 8;
            int barW    = Math.Max(2, Math.Abs(xBar1 - xBar0));
            int candleW = Math.Max(1, barW - 2);
            int wickW   = Math.Max(1, barW / 6);

            // ----------------------------------------------------------------
            //  Desenhar CVD candles
            // ----------------------------------------------------------------
            for (int b = firstBar; b <= lastBar; b++)
            {
                decimal cvdO = _cvdOpen[b];
                decimal cvdC = _cvdClose[b];
                decimal cvdH = _cvdHigh[b];
                decimal cvdL = _cvdLow[b];

                bool bullish = cvdC >= cvdO;
                Color barColor = bullish ? CorCandleAsk : CorCandleBid;

                int xCenter = ChartInfo.GetXByBar(b);
                int xLeft   = xCenter - candleW / 2;

                int yO = (int)Math.Round(CvdToY(cvdO));
                int yC = (int)Math.Round(CvdToY(cvdC));
                int yH = (int)Math.Round(CvdToY(cvdH));
                int yL = (int)Math.Round(CvdToY(cvdL));

                // Clamp ao painel
                yO = Clamp(yO, pTop, pBottom);
                yC = Clamp(yC, pTop, pBottom);
                yH = Clamp(yH, pTop, pBottom);
                yL = Clamp(yL, pTop, pBottom);

                int bodyTop    = Math.Min(yO, yC);
                int bodyBottom = Math.Max(yO, yC);
                int bodyH      = Math.Max(1, bodyBottom - bodyTop);

                // Corpo
                using (var brush = new SolidBrush(barColor))
                    context.FillRectangle(brush, new Rectangle(xLeft, bodyTop, candleW, bodyH));

                // Borda semi-transparente
                using (var pen = new Pen(Color.FromArgb(100, 0, 0, 0), 1))
                    context.DrawRectangle(pen, new Rectangle(xLeft, bodyTop, candleW, bodyH));

                // Wick superior
                if (yH < bodyTop)
                {
                    using (var pen = new Pen(barColor, wickW))
                        context.DrawLine(pen, new Point(xCenter, yH), new Point(xCenter, bodyTop));
                }

                // Wick inferior
                if (yL > bodyBottom)
                {
                    using (var pen = new Pen(barColor, wickW))
                        context.DrawLine(pen, new Point(xCenter, bodyBottom), new Point(xCenter, yL));
                }
            }

            // ----------------------------------------------------------------
            //  Desenhar linhas de divergência
            // ----------------------------------------------------------------
            using (var labelFont = new Font("Arial", 8f, FontStyle.Bold))
            {
                var visible = _divs
                    .Where(d => d.Bar1 <= lastBar && d.Bar2 >= firstBar)
                    .ToList();

                foreach (var div in visible)
                {
                    bool isHigh = div.Type == DivType.ExaustaoHigh || div.Type == DivType.AbsorcaoHigh;
                    bool isExh  = div.Type == DivType.ExaustaoHigh || div.Type == DivType.ExaustaoLow;

                    Color lineColor = isExh ? CorLinhaExaustao : CorLinhaAbsorcao;
                    string label    = isExh ? "Exaustão" : "Absorção";
                    label += isHigh ? " ▲" : " ▼";

                    int x1 = ChartInfo.GetXByBar(div.Bar1);
                    int x2 = ChartInfo.GetXByBar(div.Bar2);
                    int y1 = Clamp((int)Math.Round(CvdToY(div.Cvd1)), pTop, pBottom);
                    int y2 = Clamp((int)Math.Round(CvdToY(div.Cvd2)), pTop, pBottom);

                    // Linha tracejada entre os dois swing points do CVD
                    using (var pen = new Pen(lineColor, EspessuraLinha)
                           { DashStyle = DashStyle.Dash })
                    {
                        context.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
                    }

                    // Pequenos quadrados nos pontos de swing (mais compatível que FillEllipse)
                    int dotR = 3;
                    using (var brush = new SolidBrush(lineColor))
                    {
                        context.FillRectangle(brush, new Rectangle(x1 - dotR, y1 - dotR, dotR * 2, dotR * 2));
                        context.FillRectangle(brush, new Rectangle(x2 - dotR, y2 - dotR, dotR * 2, dotR * 2));
                    }

                    // Label próximo do swing point mais recente
                    int labelX = x2 + 6;
                    int labelY = Clamp(y2 - 10, pTop + 2, pBottom - 16);

                    using (var brush = new SolidBrush(lineColor))
                        context.DrawString(label, labelFont, brush, new PointF(labelX, labelY));
                }
            }
        }

        // ====================================================================
        //  UTILITÁRIOS
        // ====================================================================

        private static int Clamp(int value, int min, int max)
            => value < min ? min : value > max ? max : value;
    }
}
