using System.Windows;

namespace DesktopPet.Wpf;

public sealed class PetEngine
{
    private readonly Random _random = new();
    private DateTime _lastInteractionAt = DateTime.Now;
    private DateTime _nextAmbientAt = DateTime.Now.AddSeconds(3);
    private DateTime _lastFrameStepAt = DateTime.Now;
    private DateTime _nextSleepEligibleAt = DateTime.Now.AddMinutes(2);
    private DateTime? _sleepUntil;
    private DateTime _stateEndsAt = DateTime.Now.AddSeconds(2);
    private int _neglectLevel;
    private int _animationStep;
    private PetState _state = PetState.Idle;

    public bool IsSleeping { get; private set; }
    public int CurrentFrameIndex { get; private set; }
    public string CurrentSpeech { get; private set; } = "妈妈，我来陪你啦。";
    public string CurrentEmotion { get; private set; } = "♡";
    public BrushSet CurrentBrushes { get; private set; } = BrushSet.Affection;
    public double Energy { get; private set; } = 100;

    private static readonly string[] IdleLines =
    {
        "妈妈，我会一直乖乖陪着你的。",
        "妈妈，看到我有没有心情好一点呀？",
        "我在这里等妈妈，有事要记得叫我哦。",
        "只要妈妈一回头，就能看到我啦。",
        "妈妈忙自己的也没关系，我会在旁边安安静静陪着。",
        "今天也想做妈妈桌面上最乖的小女儿。",
        "妈妈要是累了，就看看我，我会陪你缓一缓。",
        "我会一直待在这里，所以妈妈不用担心找不到我。",
    };

    private static readonly string[] BlinkLines =
    {
        "眨眨眼，偷看一下妈妈。",
        "我刚刚是不是又偷偷看妈妈了。",
        "妈妈别动，我再悄悄看你一眼。",
    };

    private static readonly string[] WaveLines =
    {
        "妈妈，我在这里呀。",
        "妈妈妈妈，看我这边。",
        "挥挥手，提醒妈妈还有我在陪你。",
        "只要妈妈一看我，我就会马上有精神。",
    };

    private static readonly string[] SurpriseLines =
    {
        "诶？妈妈在叫我吗？",
        "欸欸，我有在听，妈妈。",
        "突然被妈妈注意到，会让我心里咚一下。",
        "妈妈一出声，我就会立刻竖起耳朵。",
    };

    private static readonly string[] DizzyLines =
    {
        "呀，转太快了，我有一点晕乎乎。",
        "妈妈，先让我缓一下，小脑袋在打圈圈。",
        "刚刚好像开心过头了，现在有点晕晕的。",
        "让我站稳一下，不然等会儿会继续转圈圈。",
    };

    private static readonly string[] HappyLines =
    {
        "被妈妈碰一下就会开心。",
        "嘿嘿，妈妈理我了。",
        "妈妈一理我，我就会把开心全写在脸上。",
        "果然我最喜欢被妈妈注意到了。",
    };

    private static readonly string[] ShyLines =
    {
        "妈妈一直看着我，我会有点害羞。",
        "被妈妈夸的话，我会偷偷开心很久。",
        "妈妈别一直盯着我看啦，我会不好意思的。",
        "虽然害羞，但还是想被妈妈多看几眼。",
    };

    private static readonly string[] ListenLines =
    {
        "妈妈说吧，我会认真听完的。",
        "我把小耳朵立起来啦，妈妈继续讲。",
        "妈妈慢慢说，我会把每一句都收好。",
        "不管妈妈说什么，我都会认真听着。",
    };

    private static readonly string[] LonelyLines =
    {
        "妈妈是不是忙到忘记看我了……",
        "我还在这里等妈妈，偷偷看我一眼也可以呀。",
        "再不理我，我就要开始委屈了。",
    };

    private static readonly string[] SulkLines =
    {
        "妈妈太久没理我了，我要有一点点不高兴了。",
        "哼，我先自己生一小会儿闷气，但还是会陪着妈妈。",
        "虽然有点委屈，可我还是舍不得离开妈妈。",
    };

    private static readonly string[] NeedAttentionLines =
    {
        "妈妈，你已经好久没有点我了，我真的要闹小脾气了。",
        "我都自己待这么久了，妈妈是不是该来哄一下我呀。",
        "再不理我，我就要贴在旁边偷偷不高兴了。",
        "妈妈快看看我，我已经委屈到想钻进你怀里了。",
    };

    private static readonly string[] TiredLines =
    {
        "妈妈，我玩得有一点点困了。",
        "我想先眯一小会儿，等下继续陪妈妈。",
        "今天陪妈妈陪得太认真了，眼皮开始打架了。",
        "我先补一点点电，等下再精神满满陪妈妈。",
    };

    private static readonly string[] SleepLines =
    {
        "妈妈，我先抱着梦梦睡一下。",
        "先睡一会儿，醒来再陪妈妈。",
        "晚安只是暂时的，我醒来还是会第一时间陪妈妈。",
        "妈妈不用担心，我只是去梦里打个小盹。",
    };

    private static readonly string[] WakeLines =
    {
        "我醒啦，妈妈，继续陪你。",
        "补觉结束，我回来啦，妈妈。",
        "醒来第一眼还是想先看看妈妈在不在。",
        "睡饱了，现在可以继续黏着妈妈了。",
    };

    private static readonly string[] DragLines =
    {
        "妈妈，要把我轻轻抱走哦。",
        "被妈妈拎起来也会很开心，但要轻一点呀。",
        "妈妈这是要带我去新位置待着吗？",
    };

    private static readonly string[] SoloLines =
    {
        "妈妈在忙的话，我也会乖乖等着。",
        "那我先自己发一会儿呆哦。",
        "妈妈先忙吧，我会自己找点安静的小事做。",
        "就算妈妈暂时不理我，我也会乖乖守在这里。",
    };

    private static readonly string[] PatrollingLines =
    {
        "妈妈不理我的时候，我就自己找点小事做。",
        "我先一个人练习怎么更像乖女儿。",
        "桌面巡逻中，看看有没有什么能帮妈妈分担的。",
        "我在悄悄练习，想做更让妈妈喜欢的女儿。",
    };

    private static readonly string[] StretchLines =
    {
        "我先自己伸个懒腰，等妈妈回头看看我。",
        "悄悄活动一下，免得妈妈一看我还以为我睡着啦。",
        "自己整理一下小裙摆，继续等妈妈注意我。",
        "我先偷偷转一圈，看看妈妈会不会发现我在动。",
    };

    private static readonly string[] CozyLines =
    {
        "贴着妈妈待着就会觉得很安心。",
        "今天也想在妈妈桌面上软软地待着。",
        "只要在妈妈身边，我就会慢慢变得很放松。",
        "我最喜欢这种什么都不用做，只陪着妈妈的时间。",
    };

    public PetVisual Tick()
    {
        if (IsSleeping)
        {
            if (_sleepUntil.HasValue && DateTime.Now >= _sleepUntil.Value)
            {
                WakeUp();
            }
            else
            {
                AnimateCurrentState();
            }

            return BuildVisual();
        }

        AnimateCurrentState();

        if (DateTime.Now < _stateEndsAt)
            return BuildVisual();

        var idleSeconds = (DateTime.Now - _lastInteractionAt).TotalSeconds;
        if (idleSeconds <= 8)
        {
            if (_random.NextDouble() < 0.4)
                SetBlink();
            else
                SetCozy();
        }
        else if (Energy <= 20)
        {
            BecomeTired();
        }
        else
        {
            SetIdle();
        }

        ScheduleNextAmbient();
        return BuildVisual();
    }

    public PetVisual Interact(string action)
    {
        if (IsSleeping && !string.Equals(action, "sleep", StringComparison.OrdinalIgnoreCase))
        {
            WakeUp();
            ScheduleNextAmbient();
            return BuildVisual();
        }

        switch (action)
        {
            case "drag":
                MarkInteraction(6);
                SetDrag();
                break;
            case "wave":
                MarkInteraction(16);
                SetWave();
                break;
            case "surprised":
                MarkInteraction(18);
                SetSurprised();
                break;
            case "dizzy":
                MarkInteraction(18);
                SetDizzy();
                break;
            case "happy":
                MarkInteraction(18);
                SetHappy();
                break;
            case "shy":
                MarkInteraction(14);
                SetShy();
                break;
            case "listen":
                MarkInteraction(14);
                SetListen();
                break;
            case "sleep":
                FallAsleep();
                break;
            case "idle":
                WakeUp();
                break;
            default:
                MarkInteraction(10);
                SetIdle();
                break;
        }

        ScheduleNextAmbient();
        return BuildVisual();
    }

    public PetVisual AutoBehavior()
    {
        if (IsSleeping)
        {
            if (_sleepUntil.HasValue && DateTime.Now >= _sleepUntil.Value)
                WakeUp();

            return BuildVisual();
        }

        if (DateTime.Now < _stateEndsAt || DateTime.Now < _nextAmbientAt)
            return BuildVisual();

        var idleSeconds = (DateTime.Now - _lastInteractionAt).TotalSeconds;

        if (idleSeconds > 72 && _neglectLevel < 3)
        {
            _neglectLevel = 3;
            SetNeedAttention();
            ScheduleNextAmbient();
            return BuildVisual();
        }

        if (idleSeconds > 46 && _neglectLevel < 2)
        {
            _neglectLevel = 2;
            SetSulk();
            ScheduleNextAmbient();
            return BuildVisual();
        }

        if (idleSeconds > 24 && _neglectLevel < 1)
        {
            _neglectLevel = 1;
            SetLonely();
            ScheduleNextAmbient();
            return BuildVisual();
        }

        var canSleepNow = DateTime.Now >= _nextSleepEligibleAt;
        if ((Energy <= 3 && idleSeconds > 40 && canSleepNow) || (idleSeconds > 115 && canSleepNow))
        {
            BecomeTired();
            FallAsleep();
            return BuildVisual();
        }

        if (idleSeconds > 20)
        {
            Energy = Math.Max(0, Energy - 4);
            switch (_random.Next(0, 4))
            {
                case 0:
                    SetSolo();
                    break;
                case 1:
                    SetPatrolling();
                    break;
                case 2:
                    SetStretch();
                    break;
                default:
                    SetWave();
                    break;
            }

            ScheduleNextAmbient();
            return BuildVisual();
        }

        if (idleSeconds > 10 && Energy > 35)
        {
            Energy = Math.Max(0, Energy - 3);
            switch (_random.Next(0, 4))
            {
                case 0:
                    SetCozy();
                    break;
                case 1:
                    SetBlink();
                    break;
                case 2:
                    SetShy();
                    break;
                default:
                    SetListen();
                    break;
            }

            ScheduleNextAmbient();
            return BuildVisual();
        }

        switch (_random.Next(0, 6))
        {
            case 0:
                SetBlink();
                break;
            case 1:
                SetWave();
                break;
            case 2:
                SetShy();
                break;
            case 3:
                SetListen();
                break;
            case 4:
                SetSolo();
                break;
            default:
                SetCozy();
                break;
        }

        ScheduleNextAmbient();
        return BuildVisual();
    }

    public void RecoverEnergy()
    {
        Energy = Math.Min(100, Energy + (IsSleeping ? 25 : 6));
    }

    private void MarkInteraction(double energyCost)
    {
        _lastInteractionAt = DateTime.Now;
        _neglectLevel = 0;
        Energy = Math.Max(0, Energy - energyCost);
    }

    private void ScheduleNextAmbient()
    {
        _nextAmbientAt = DateTime.Now.AddSeconds(_random.Next(4, 9));
    }

    private void BeginState(PetState state, int minSeconds, int maxSeconds)
    {
        _state = state;
        _animationStep = 0;
        _lastFrameStepAt = DateTime.Now;
        _stateEndsAt = DateTime.Now.AddSeconds(_random.Next(minSeconds, maxSeconds + 1));
    }

    private void AnimateCurrentState()
    {
        var now = DateTime.Now;
        if ((now - _lastFrameStepAt).TotalMilliseconds < 420)
            return;

        _lastFrameStepAt = now;
        _animationStep++;

        switch (_state)
        {
            case PetState.Idle:
                CurrentFrameIndex = _animationStep % 4 == 1 ? 2 : 0;
                break;
            case PetState.Blink:
                CurrentFrameIndex = _animationStep % 2 == 0 ? 0 : 2;
                break;
            case PetState.Wave:
            case PetState.Patrolling:
                CurrentFrameIndex = _animationStep % 2 == 0 ? 3 : 12;
                break;
            case PetState.Listen:
            case PetState.Surprised:
                CurrentFrameIndex = _animationStep % 3 == 1 ? 10 : 0;
                break;
            case PetState.Happy:
            case PetState.Shy:
            case PetState.Cozy:
                CurrentFrameIndex = _animationStep % 2 == 0 ? 14 : 0;
                break;
            case PetState.Dizzy:
                CurrentFrameIndex = _animationStep % 2 == 0 ? 15 : 10;
                break;
            case PetState.Lonely:
            case PetState.Sulk:
            case PetState.NeedAttention:
            case PetState.Solo:
                CurrentFrameIndex = _animationStep % 3 == 1 ? 2 : 0;
                break;
            case PetState.Tired:
                CurrentFrameIndex = _animationStep % 3 == 1 ? 2 : 0;
                break;
            case PetState.Sleep:
                CurrentFrameIndex = _animationStep % 2 == 0 ? 9 : 2;
                break;
            case PetState.Drag:
                CurrentFrameIndex = _animationStep % 2 == 0 ? 3 : 12;
                break;
            case PetState.Stretch:
                CurrentFrameIndex = _animationStep % 2 == 0 ? 14 : 3;
                break;
            default:
                break;
        }
    }

    private void SetIdle()
    {
        BeginState(PetState.Idle, 2, 4);
        CurrentFrameIndex = 0;
        CurrentSpeech = Pick(IdleLines);
        CurrentEmotion = "♡";
        CurrentBrushes = BrushSet.Affection;
    }

    private void SetBlink()
    {
        BeginState(PetState.Blink, 1, 2);
        CurrentFrameIndex = 2;
        CurrentSpeech = Pick(BlinkLines);
        CurrentEmotion = "...";
        CurrentBrushes = BrushSet.Affection;
    }

    private void SetWave()
    {
        BeginState(PetState.Wave, 2, 3);
        CurrentFrameIndex = _random.Next(0, 2) == 0 ? 3 : 12;
        CurrentSpeech = Pick(WaveLines);
        CurrentEmotion = "♪";
        CurrentBrushes = BrushSet.Affection;
    }

    private void SetSurprised()
    {
        BeginState(PetState.Surprised, 2, 3);
        CurrentFrameIndex = 10;
        CurrentSpeech = Pick(SurpriseLines);
        CurrentEmotion = "!";
        CurrentBrushes = BrushSet.Surprised;
    }

    private void SetDizzy()
    {
        BeginState(PetState.Dizzy, 2, 4);
        CurrentFrameIndex = 15;
        CurrentSpeech = Pick(DizzyLines);
        CurrentEmotion = "✺";
        CurrentBrushes = BrushSet.Surprised;
    }

    private void SetHappy()
    {
        BeginState(PetState.Happy, 2, 3);
        CurrentFrameIndex = 14;
        CurrentSpeech = Pick(HappyLines);
        CurrentEmotion = "♥";
        CurrentBrushes = BrushSet.Happy;
    }

    private void SetShy()
    {
        BeginState(PetState.Shy, 2, 3);
        CurrentFrameIndex = 14;
        CurrentSpeech = Pick(ShyLines);
        CurrentEmotion = "✿";
        CurrentBrushes = BrushSet.Affection;
    }

    private void SetListen()
    {
        BeginState(PetState.Listen, 2, 3);
        CurrentFrameIndex = 10;
        CurrentSpeech = Pick(ListenLines);
        CurrentEmotion = "◔";
        CurrentBrushes = BrushSet.Affection;
    }

    private void SetLonely()
    {
        BeginState(PetState.Lonely, 3, 4);
        CurrentFrameIndex = 2;
        CurrentSpeech = Pick(LonelyLines);
        CurrentEmotion = "…";
        CurrentBrushes = BrushSet.Surprised;
    }

    private void SetSulk()
    {
        BeginState(PetState.Sulk, 3, 5);
        CurrentFrameIndex = 2;
        CurrentSpeech = Pick(SulkLines);
        CurrentEmotion = "☁";
        CurrentBrushes = BrushSet.Surprised;
    }

    private void SetNeedAttention()
    {
        BeginState(PetState.NeedAttention, 4, 6);
        CurrentFrameIndex = 10;
        CurrentSpeech = Pick(NeedAttentionLines);
        CurrentEmotion = "﹏";
        CurrentBrushes = BrushSet.Surprised;
    }

    private void BecomeTired()
    {
        BeginState(PetState.Tired, 2, 4);
        CurrentFrameIndex = 0;
        CurrentSpeech = Pick(TiredLines);
        CurrentEmotion = "...";
        CurrentBrushes = BrushSet.Sleepy;
    }

    private void FallAsleep()
    {
        IsSleeping = true;
        BeginState(PetState.Sleep, 18, 28);
        _nextSleepEligibleAt = DateTime.Now.AddMinutes(3);
        _sleepUntil = DateTime.Now.AddSeconds(_random.Next(18, 29));
        CurrentFrameIndex = 9;
        CurrentSpeech = Pick(SleepLines);
        CurrentEmotion = "Zz";
        CurrentBrushes = BrushSet.Sleepy;
    }

    private void WakeUp()
    {
        IsSleeping = false;
        _sleepUntil = null;
        _lastInteractionAt = DateTime.Now;
        _neglectLevel = 0;
        Energy = Math.Min(100, Energy + 35);
        BeginState(PetState.Idle, 3, 4);
        CurrentFrameIndex = 0;
        CurrentSpeech = Pick(WakeLines);
        CurrentEmotion = "✦";
        CurrentBrushes = BrushSet.Affection;
    }

    private void SetDrag()
    {
        BeginState(PetState.Drag, 1, 2);
        CurrentFrameIndex = 3;
        CurrentSpeech = Pick(DragLines);
        CurrentEmotion = "♡";
        CurrentBrushes = BrushSet.Affection;
    }

    private void SetSolo()
    {
        BeginState(PetState.Solo, 3, 4);
        CurrentFrameIndex = 2;
        CurrentSpeech = Pick(SoloLines);
        CurrentEmotion = "…";
        CurrentBrushes = BrushSet.Affection;
    }

    private void SetPatrolling()
    {
        BeginState(PetState.Patrolling, 3, 4);
        CurrentFrameIndex = 3;
        CurrentSpeech = Pick(PatrollingLines);
        CurrentEmotion = "↺";
        CurrentBrushes = BrushSet.Affection;
    }

    private void SetStretch()
    {
        BeginState(PetState.Stretch, 2, 4);
        CurrentFrameIndex = _random.NextDouble() < 0.5 ? 3 : 14;
        CurrentSpeech = Pick(StretchLines);
        CurrentEmotion = "～";
        CurrentBrushes = BrushSet.Affection;
    }

    private void SetCozy()
    {
        BeginState(PetState.Cozy, 2, 4);
        CurrentFrameIndex = _random.NextDouble() < 0.5 ? 0 : 14;
        CurrentSpeech = Pick(CozyLines);
        CurrentEmotion = "♡";
        CurrentBrushes = BrushSet.Affection;
    }

    private string Pick(IReadOnlyList<string> lines) => lines[_random.Next(lines.Count)];

    private PetVisual BuildVisual() =>
        new(CurrentFrameIndex, CurrentSpeech, CurrentEmotion, CurrentBrushes);
}

public sealed record PetVisual(int FrameIndex, string Speech, string Emotion, BrushSet Brushes);

public sealed record BrushSet(string BubbleHex, string TextHex, string EmotionHex)
{
    public static readonly BrushSet Affection = new("#FFF0F6", "#8D2D59", "#FF5E98");
    public static readonly BrushSet Happy = new("#FFF9D6", "#7C4C00", "#FF8B2B");
    public static readonly BrushSet Surprised = new("#FFEBF0", "#8B2951", "#FF5F7B");
    public static readonly BrushSet Sleepy = new("#E8EFFF", "#3156A5", "#5B76DB");
}

public enum PetState
{
    Idle,
    Blink,
    Wave,
    Surprised,
    Dizzy,
    Happy,
    Shy,
    Listen,
    Lonely,
    Sulk,
    NeedAttention,
    Tired,
    Sleep,
    Drag,
    Solo,
    Patrolling,
    Stretch,
    Cozy,
}
