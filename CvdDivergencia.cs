// ============================================================================
//  CVD With Exaus/Absor. Pereira  —  ATAS Platform  (SDK 10)
//  Deteta Exaustão e Absorção entre preço e CVD no NQ (1 min),
//  com confirmação simultânea opcional no ES.
//  Divergências desenhadas diretamente no chart principal.
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

        [Display(Name = "Cor Linha Exaustão", GroupName = "Divergências", Order = 0)]
        public Color CorLinhaExaustao { get; set; } = Color.Orange;

        [Display(Name = "Cor Linha Absorção", GroupName = "Divergências", Order = 1)]
        public Color CorLinhaAbsorcao { get; set; } = Color.DeepSkyBlue;

        [Display(Name = "Espessura das Linhas", GroupName = "Divergências", Order = 2)]
        [Range(1, 5)]
        public int EspessuraLinha { get; set; } = 2;

        [Display(Name = "Mostrar Etiquetas de Texto", GroupName = "Divergências", Order = 3)]
        public bool MostrarEtiquetas { get; set; } = true;

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
            public decimal  Price1, Price2;  // preço (High ou Low) de cada swing
        }

        // ====================================================================
        //  SÉRIES DE DADOS – CVD (NQ)  [apenas para cálculo interno]
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
        //  ESTADO INTERNO – sessão
        // ====================================================================

        private int _sessionStartBar = 0;

        // ====================================================================
        //  ESTADO INTERNO – ES (background)
        // ====================================================================

        private object                    _esSource;   // SecurityDataSource — ⚠ VERIFICAR (ver StartEsSource)
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
            _cvdClose = new ValueDataSeries("CVD Close") { IsHidden = true, VisualType = VisualMode.Hide };
            _cvdOpen  = new ValueDataSeries("CVD Open")  { IsHidden = true, VisualType = VisualMode.Hide };
            _cvdHigh  = new ValueDataSeries("CVD High")  { IsHidden = true, VisualType = VisualMode.Hide };
            _cvdLow   = new ValueDataSeries("CVD Low")   { IsHidden = true, VisualType = VisualMode.Hide };

            DataSeries[0] = _cvdClose;
            DataSeries.Add(_cvdOpen);
            DataSeries.Add(_cvdHigh);
            DataSeries.Add(_cvdLow);

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

            // Detetar início de nova sessão (mudança de dia) — resetar CVD a 0 como o CVD nativo do ATAS
            bool isNewSession = false;
            if (bar > 0)
            {
                var prev = GetCandle(bar - 1);
                isNewSession = c.Time.Date != prev.Time.Date;
            }

            if (bar == 0 || isNewSession)
            {
                _sessionStartBar = bar;
                _cvdOpen[bar]  = 0m;
                _cvdClose[bar] = c.Delta;
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

            // Na barra de início de sessão, _cvdOpen=0, por isso _cvdHigh≥0 e _cvdLow≤0.
            // Isso faz o ATAS incluir 0 na escala → linha verde no topo + escala de 0 a -5K.
            // Corrigir: usar apenas o close para que 0 não apareça na escala.
            if (bar == _sessionStartBar)
            {
                _cvdHigh[bar] = _cvdClose[bar];
                _cvdLow[bar]  = _cvdClose[bar];
            }

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
                Type   = type.Value,
                Bar1   = prev.Bar,    Bar2   = curr.Bar,
                Cvd1   = prev.CvdVal, Cvd2   = curr.CvdVal,
                Price1 = prev.Price,  Price2 = curr.Price
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
                Type   = type.Value,
                Bar1   = prev.Bar,    Bar2   = curr.Bar,
                Cvd1   = prev.CvdVal, Cvd2   = curr.CvdVal,
                Price1 = prev.Price,  Price2 = curr.Price
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

            // ⚠ VERIFICAR: 'SecurityDataSource' não foi encontrado em nenhum dos DLLs
            // referenciados. Para ativar o ES simultâneo:
            //   1. Descobrir o nome/namespace correto via IntelliSense ou exemplos SDK
            //      (pode ser ATAS.Indicators.Technical.SecurityDataSource, OFT.Core.SecurityDataSource, etc.)
            //   2. Adicionar o using correspondente no topo do ficheiro
            //   3. Mudar o tipo do campo _esSource para o tipo concreto
            //   4. Descomentar e corrigir as linhas abaixo:
            //
            //   _esSource = new SecurityDataSource("ES", CurrentSeries.Period);
            //   // ⚠ VERIFICAR nome do evento: NewCandleCreated / NewCandle / CandleUpdated
            //   _esSource.NewCandleCreated += OnEsCandle;
            //   _esSource.Start();
        }

        private void DisposeEsSource()
        {
            // ⚠ VERIFICAR: quando StartEsSource estiver implementado, acrescentar aqui:
            //   _esSource.NewCandleCreated -= OnEsCandle;
            //   _esSource.Stop();
            //   (_esSource as IDisposable)?.Dispose();
            _esSource = null;
        }

        // EventArgs (base) é contra-variante: compatível com qualquer EventHandler<T> onde T : EventArgs.
        // Acesso à candle via reflexão evita dependência do nome exacto do tipo EventArgs do SDK.
        // ⚠ VERIFICAR: se o SDK expõe a candle como e.Candle, e.Bar, e.Value ou outra propriedade,
        //              substitua o GetProperty("Candle") pelo nome correto.
        private void OnEsCandle(object sender, EventArgs e)
        {
            var candleProp = e.GetType().GetProperty("Candle");
            if (candleProp == null) return;
            var c = candleProp.GetValue(e) as IndicatorCandle;
            if (c == null) return;

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
        //  RENDERING — LINHAS DE DIVERGÊNCIA NO CHART PRINCIPAL
        // ====================================================================

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (layout != DrawingLayouts.Final) return;
            if (CurrentBar <= 0) return;

            var reg = Container.Region;
            double bw = Math.Max(1.0, (double)ChartInfo.PriceChartContainer.BarsWidth);

            int xCurr = ChartInfo.GetXByBar(CurrentBar, false);
            double barsOffRight = (xCurr - reg.Right) / bw;
            int lastBar = barsOffRight > 0.5
                ? Math.Max(0, CurrentBar - (int)barsOffRight)
                : CurrentBar;

            int firstBar = 0;
            for (int b = lastBar - 1; b >= 0; b--)
            {
                if (ChartInfo.GetXByBar(b, false) < reg.Left)
                {
                    firstBar = b + 1;
                    break;
                }
            }

            var labelFont = new RenderFont("Arial", 8);
            var visible   = _divs.Where(d => d.Bar1 <= lastBar && d.Bar2 >= firstBar).ToList();

            foreach (var div in visible)
            {
                bool isHigh = div.Type == DivType.ExaustaoHigh || div.Type == DivType.AbsorcaoHigh;
                bool isExh  = div.Type == DivType.ExaustaoHigh || div.Type == DivType.ExaustaoLow;

                Color  lineColor = isExh ? CorLinhaExaustao : CorLinhaAbsorcao;
                string label     = isExh ? "Exaustao" : "Absorcao";
                label += isHigh ? " ^" : " v";

                int x1 = ChartInfo.GetXByBar(div.Bar1, false);
                int x2 = ChartInfo.GetXByBar(div.Bar2, false);
                int y1 = ChartInfo.GetYByPrice(div.Price1);
                int y2 = ChartInfo.GetYByPrice(div.Price2);

                var linePen = new RenderPen(lineColor, EspessuraLinha);
                linePen.DashStyle = DashStyle.Dash;
                context.DrawLine(linePen, x1, y1, x2, y2);

                if (MostrarEtiquetas)
                {
                    int labelX = x2 + 6;
                    int labelY = y2 - 10;
                    context.DrawString(label, labelFont, lineColor, labelX, labelY);
                }
            }
        }

        // ====================================================================
        //  UTILITÁRIOS
        // ====================================================================

        private static int Clamp(int v, int lo, int hi)
            => v < lo ? lo : v > hi ? hi : v;
    }
}
