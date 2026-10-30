// Adapted from NWaves RobotEffect / OverlapAddFilter, Copyright (c) 2017 Tim.
// Original MIT notice: THIRD_PARTY_LICENSES/NWaves-MIT.txt.
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>Full-wet zero-phase overlap-add robotization using NAudio's reusable real FFT.</summary>
internal sealed class RobotSpectralCore
{
    private readonly int _hop, _size, _overlap;
    private readonly float _gain;
    private readonly float[] _window, _delay, _output, _saved, _time;
    private readonly Complex[] _spectrum;
    private readonly FftProcessor _fft;
    private int _inputOffset, _outputOffset;

    internal int LatencySamples => _size - 1;

    internal RobotSpectralCore(int hopSize, int fftSize)
    {
        if (fftSize < 2 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(fftSize));
        if (hopSize < 1 || hopSize > fftSize)
            throw new ArgumentOutOfRangeException(nameof(hopSize));
        _hop = hopSize;
        _size = fftSize;
        _overlap = fftSize - hopSize;
        _fft = new FftProcessor(fftSize);
        _window = new float[fftSize];
        _delay = new float[fftSize];
        _output = new float[fftSize];
        _saved = new float[_overlap];
        _time = new float[fftSize];
        _spectrum = new Complex[fftSize / 2 + 1];
        double step = 2 * Math.PI / (fftSize - 1);
        for (int i = 0; i < fftSize; i++)
            _window[i] = (float)(.5 * (1 - Math.Cos(i * step)));
        float gain = 1 / (fftSize * _window.Select(w => w * w).Sum() / hopSize);
        gain *= (float)Math.PI;
        // NAudio scales its forward transform by 1/N; NWaves leaves both transforms unscaled.
        _gain = gain * fftSize;
        _inputOffset = _overlap;
    }

    internal float Process(float sample)
    {
        _delay[_inputOffset++] = sample;
        if (_inputOffset == _size) ProcessFrame();
        return _output[_outputOffset++];
    }

    private void ProcessFrame()
    {
        for (int i = 0; i < _size; i++) _time[i] = _delay[i] * _window[i];
        _fft.RealForward(_time, _spectrum);
        for (int i = 0; i < _spectrum.Length; i++)
        {
            float re = _spectrum[i].X, im = _spectrum[i].Y;
            _spectrum[i] = new Complex { X = (float)Math.Sqrt(re * re + im * im), Y = 0 };
        }
        _fft.RealInverse(_spectrum, _time);
        for (int i = 0; i < _size; i++) _output[i] = _time[i] * _window[i];
        for (int i = 0; i < _overlap; i++) _output[i] += _saved[i];
        _output.AsSpan(_hop, _overlap).CopyTo(_saved);
        for (int i = 0; i < _hop; i++) _output[i] *= _gain;
        _delay.AsSpan(_hop, _overlap).CopyTo(_delay);
        _inputOffset = _overlap;
        _outputOffset = 0;
    }
}
