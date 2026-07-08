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
    public PropSet CurrentProp { get; private set; } = PropSet.Ribbon;
    public double Energy { get; private set; } = 100;

    private static readonly PropSet[] SoftProps =
    {
        PropSet.Ribbon,
        PropSet.Star,
        PropSet.HeartCandy,
        PropSet.Tea,
    };

    private static readonly PropSet[] WarmProps =
    {
        PropSet.HeartCandy,
        PropSet.Ribbon,
        PropSet.Cookie,
    };

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
        "妈妈在做自己的事时，我就负责在旁边软乎乎陪着你。",
        "只要能让妈妈一转头就看到我，我今天就已经很满足啦。",
        "我想把桌面这一小块地方，变成妈妈一看就会放松的角落。",
        "妈妈安心忙吧，我会守着这里等你偶尔看看我。",
    };

    private static readonly string[] BlinkLines =
    {
        "眨眨眼，偷看一下妈妈。",
        "我刚刚是不是又偷偷看妈妈了。",
        "妈妈别动，我再悄悄看你一眼。",
        "先轻轻眨一下眼，装作自己一点也不想引起妈妈注意。",
        "我在用最乖的表情，等妈妈把目光分给我一点点。",
    };

    private static readonly string[] WaveLines =
    {
        "妈妈，我在这里呀。",
        "妈妈妈妈，看我这边。",
        "挥挥手，提醒妈妈还有我在陪你。",
        "只要妈妈一看我，我就会马上有精神。",
        "小手举高高，想让妈妈一眼就注意到我。",
        "我悄悄朝妈妈晃一晃，希望你现在刚好会看过来。",
    };

    private static readonly string[] SurpriseLines =
    {
        "诶？妈妈在叫我吗？",
        "欸欸，我有在听，妈妈。",
        "突然被妈妈注意到，会让我心里咚一下。",
        "妈妈一出声，我就会立刻竖起耳朵。",
        "只要妈妈有一点动静，我就会立刻抬头找你。",
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
        "只要妈妈肯理我一下，我就能自己开心很久很久。",
        "我现在是那种，连裙摆都在偷偷高兴的状态。",
    };

    private static readonly string[] ShyLines =
    {
        "妈妈一直看着我，我会有点害羞。",
        "被妈妈夸的话，我会偷偷开心很久。",
        "妈妈别一直盯着我看啦，我会不好意思的。",
        "虽然害羞，但还是想被妈妈多看几眼。",
        "妈妈如果再温柔一点看我，我就真的要躲起来啦。",
        "嘴上说着害羞，其实心里还是想让妈妈多看看。",
    };

    private static readonly string[] ListenLines =
    {
        "妈妈说吧，我会认真听完的。",
        "我把小耳朵立起来啦，妈妈继续讲。",
        "妈妈慢慢说，我会把每一句都收好。",
        "不管妈妈说什么，我都会认真听着。",
        "妈妈说的话，我会像小纸条一样一张张收进心里。",
        "只要是妈妈的声音，我就会自动变得很专心。",
    };

    private static readonly string[] LonelyLines =
    {
        "妈妈是不是忙到忘记看我了……",
        "我还在这里等妈妈，偷偷看我一眼也可以呀。",
        "再不理我，我就要开始委屈了。",
        "我没有乱跑，只是在原地等妈妈想起我。",
        "只要妈妈肯看我一眼，我的委屈就会马上变小很多。",
    };

    private static readonly string[] SulkLines =
    {
        "妈妈太久没理我了，我要有一点点不高兴了。",
        "哼，我先自己生一小会儿闷气，但还是会陪着妈妈。",
        "虽然有点委屈，可我还是舍不得离开妈妈。",
        "我把小脾气抱在怀里了，但还是会继续陪着妈妈。",
        "这次真的有一点点闹别扭了，要妈妈哄一下才行。",
    };

    private static readonly string[] NeedAttentionLines =
    {
        "妈妈，你已经好久没有点我了，我真的要闹小脾气了。",
        "我都自己待这么久了，妈妈是不是该来哄一下我呀。",
        "再不理我，我就要贴在旁边偷偷不高兴了。",
        "妈妈快看看我，我已经委屈到想钻进你怀里了。",
        "我现在是努力忍着不闹，但真的很想让妈妈抱一下。",
        "再不给我一点注意力，我就要把委屈全挂在脸上了。",
    };

    private static readonly string[] TiredLines =
    {
        "妈妈，我玩得有一点点困了。",
        "我想先眯一小会儿，等下继续陪妈妈。",
        "今天陪妈妈陪得太认真了，眼皮开始打架了。",
        "我先补一点点电，等下再精神满满陪妈妈。",
        "我想抱着软软的小枕头，悄悄靠一会儿。",
        "先打个小哈欠，等恢复精神了就继续黏妈妈。",
    };

    private static readonly string[] SleepLines =
    {
        "妈妈，我先抱着梦梦睡一下。",
        "先睡一会儿，醒来再陪妈妈。",
        "晚安只是暂时的，我醒来还是会第一时间陪妈妈。",
        "妈妈不用担心，我只是去梦里打个小盹。",
        "我把小被角拉好啦，睡醒再继续守着妈妈。",
        "先去做一个软软的梦，梦里也会记得妈妈。",
    };

    private static readonly string[] WakeLines =
    {
        "我醒啦，妈妈，继续陪你。",
        "补觉结束，我回来啦，妈妈。",
        "醒来第一眼还是想先看看妈妈在不在。",
        "睡饱了，现在可以继续黏着妈妈了。",
        "我揉揉眼睛回来啦，第一件事当然还是找妈妈。",
    };

    private static readonly string[] DragLines =
    {
        "妈妈，要把我轻轻抱走哦。",
        "被妈妈拎起来也会很开心，但要轻一点呀。",
        "妈妈这是要带我去新位置待着吗？",
        "妈妈抱我移动的时候，我会乖乖缩起来一点。",
    };

    private static readonly string[] SoloLines =
    {
        "妈妈在忙的话，我也会乖乖等着。",
        "那我先自己发一会儿呆哦。",
        "妈妈先忙吧，我会自己找点安静的小事做。",
        "就算妈妈暂时不理我，我也会乖乖守在这里。",
        "我先自己抱着小心思坐一会儿，等妈妈空下来。",
        "一个人安静待着的时候，我也会想象妈妈待会儿会不会看我。",
    };

    private static readonly string[] PatrollingLines =
    {
        "妈妈不理我的时候，我就自己找点小事做。",
        "我先一个人练习怎么更像乖女儿。",
        "桌面巡逻中，看看有没有什么能帮妈妈分担的。",
        "我在悄悄练习，想做更让妈妈喜欢的女儿。",
        "我先抱着自己的小任务转一圈，顺便看看妈妈有没有偷看我。",
        "巡逻一下桌面角落，确保这里还像个舒服的小窝。",
    };

    private static readonly string[] StretchLines =
    {
        "我先自己伸个懒腰，等妈妈回头看看我。",
        "悄悄活动一下，免得妈妈一看我还以为我睡着啦。",
        "自己整理一下小裙摆，继续等妈妈注意我。",
        "我先偷偷转一圈，看看妈妈会不会发现我在动。",
        "我把蝴蝶结理整齐一点，这样妈妈看过来会更喜欢。",
        "先活动一下小肩膀，再继续端端正正陪着妈妈。",
    };

    private static readonly string[] CozyLines =
    {
        "贴着妈妈待着就会觉得很安心。",
        "今天也想在妈妈桌面上软软地待着。",
        "只要在妈妈身边，我就会慢慢变得很放松。",
        "我最喜欢这种什么都不用做，只陪着妈妈的时间。",
        "我抱着一点点小甜意，安安静静陪在妈妈旁边。",
        "像这样轻轻待着的时候，我会觉得整个世界都很软。",
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
            case "cozy":
                MarkInteraction(8);
                SetCozy();
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
        CurrentProp = Pick(SoftProps);
    }

    private void SetBlink()
    {
        BeginState(PetState.Blink, 1, 2);
        CurrentFrameIndex = 2;
        CurrentSpeech = Pick(BlinkLines);
        CurrentEmotion = "...";
        CurrentBrushes = BrushSet.Affection;
        CurrentProp = PropSet.Star;
    }

    private void SetWave()
    {
        BeginState(PetState.Wave, 2, 3);
        CurrentFrameIndex = _random.Next(0, 2) == 0 ? 3 : 12;
        CurrentSpeech = Pick(WaveLines);
        CurrentEmotion = "♪";
        CurrentBrushes = BrushSet.Affection;
        CurrentProp = PropSet.Ribbon;
    }

    private void SetSurprised()
    {
        BeginState(PetState.Surprised, 2, 3);
        CurrentFrameIndex = 10;
        CurrentSpeech = Pick(SurpriseLines);
        CurrentEmotion = "!";
        CurrentBrushes = BrushSet.Surprised;
        CurrentProp = PropSet.Bell;
    }

    private void SetDizzy()
    {
        BeginState(PetState.Dizzy, 2, 4);
        CurrentFrameIndex = 15;
        CurrentSpeech = Pick(DizzyLines);
        CurrentEmotion = "✺";
        CurrentBrushes = BrushSet.Surprised;
        CurrentProp = PropSet.Spiral;
    }

    private void SetHappy()
    {
        BeginState(PetState.Happy, 2, 3);
        CurrentFrameIndex = 14;
        CurrentSpeech = Pick(HappyLines);
        CurrentEmotion = "♥";
        CurrentBrushes = BrushSet.Happy;
        CurrentProp = Pick(WarmProps);
    }

    private void SetShy()
    {
        BeginState(PetState.Shy, 2, 3);
        CurrentFrameIndex = 14;
        CurrentSpeech = Pick(ShyLines);
        CurrentEmotion = "✿";
        CurrentBrushes = BrushSet.Affection;
        CurrentProp = PropSet.Ribbon;
    }

    private void SetListen()
    {
        BeginState(PetState.Listen, 2, 3);
        CurrentFrameIndex = 10;
        CurrentSpeech = Pick(ListenLines);
        CurrentEmotion = "◔";
        CurrentBrushes = BrushSet.Affection;
        CurrentProp = PropSet.Letter;
    }

    private void SetLonely()
    {
        BeginState(PetState.Lonely, 3, 4);
        CurrentFrameIndex = 2;
        CurrentSpeech = Pick(LonelyLines);
        CurrentEmotion = "…";
        CurrentBrushes = BrushSet.Surprised;
        CurrentProp = PropSet.Cloud;
    }

    private void SetSulk()
    {
        BeginState(PetState.Sulk, 3, 5);
        CurrentFrameIndex = 2;
        CurrentSpeech = Pick(SulkLines);
        CurrentEmotion = "☁";
        CurrentBrushes = BrushSet.Surprised;
        CurrentProp = PropSet.Cloud;
    }

    private void SetNeedAttention()
    {
        BeginState(PetState.NeedAttention, 4, 6);
        CurrentFrameIndex = 10;
        CurrentSpeech = Pick(NeedAttentionLines);
        CurrentEmotion = "﹏";
        CurrentBrushes = BrushSet.Surprised;
        CurrentProp = PropSet.HeartCandy;
    }

    private void BecomeTired()
    {
        BeginState(PetState.Tired, 2, 4);
        CurrentFrameIndex = 0;
        CurrentSpeech = Pick(TiredLines);
        CurrentEmotion = "...";
        CurrentBrushes = BrushSet.Sleepy;
        CurrentProp = PropSet.Tea;
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
        CurrentProp = PropSet.Pillow;
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
        CurrentProp = PropSet.Star;
    }

    private void SetDrag()
    {
        BeginState(PetState.Drag, 1, 2);
        CurrentFrameIndex = 3;
        CurrentSpeech = Pick(DragLines);
        CurrentEmotion = "♡";
        CurrentBrushes = BrushSet.Affection;
        CurrentProp = PropSet.Ribbon;
    }

    private void SetSolo()
    {
        BeginState(PetState.Solo, 3, 4);
        CurrentFrameIndex = 2;
        CurrentSpeech = Pick(SoloLines);
        CurrentEmotion = "…";
        CurrentBrushes = BrushSet.Affection;
        CurrentProp = PropSet.Cookie;
    }

    private void SetPatrolling()
    {
        BeginState(PetState.Patrolling, 3, 4);
        CurrentFrameIndex = 3;
        CurrentSpeech = Pick(PatrollingLines);
        CurrentEmotion = "↺";
        CurrentBrushes = BrushSet.Affection;
        CurrentProp = PropSet.Flag;
    }

    private void SetStretch()
    {
        BeginState(PetState.Stretch, 2, 4);
        CurrentFrameIndex = _random.NextDouble() < 0.5 ? 3 : 14;
        CurrentSpeech = Pick(StretchLines);
        CurrentEmotion = "～";
        CurrentBrushes = BrushSet.Affection;
        CurrentProp = PropSet.Ribbon;
    }

    private void SetCozy()
    {
        BeginState(PetState.Cozy, 2, 4);
        CurrentFrameIndex = _random.NextDouble() < 0.5 ? 0 : 14;
        CurrentSpeech = Pick(CozyLines);
        CurrentEmotion = "♡";
        CurrentBrushes = BrushSet.Affection;
        CurrentProp = Pick(SoftProps);
    }

    private string Pick(IReadOnlyList<string> lines) => lines[_random.Next(lines.Count)];

    private PropSet Pick(IReadOnlyList<PropSet> props) => props[_random.Next(props.Count)];

    private PetVisual BuildVisual() =>
        new(CurrentFrameIndex, CurrentSpeech, CurrentEmotion, CurrentBrushes, CurrentProp);
}

public sealed record PetVisual(int FrameIndex, string Speech, string Emotion, BrushSet Brushes, PropSet Prop);

public sealed record BrushSet(string BubbleHex, string TextHex, string EmotionHex)
{
    public static readonly BrushSet Affection = new("#FFF0F6", "#8D2D59", "#FF5E98");
    public static readonly BrushSet Happy = new("#FFF9D6", "#7C4C00", "#FF8B2B");
    public static readonly BrushSet Surprised = new("#FFEBF0", "#8B2951", "#FF5F7B");
    public static readonly BrushSet Sleepy = new("#E8EFFF", "#3156A5", "#5B76DB");
}

public sealed record PropSet(string Text, string BackgroundHex, string ForegroundHex)
{
    public static readonly PropSet Ribbon = new("结", "#FFF6FB", "#D85A95");
    public static readonly PropSet Star = new("星", "#FFF8DA", "#D98A1F");
    public static readonly PropSet HeartCandy = new("糖", "#FFF1F7", "#E35A88");
    public static readonly PropSet Tea = new("茶", "#EEF7E8", "#5E8C44");
    public static readonly PropSet Pillow = new("枕", "#EEF1FF", "#5A6ED6");
    public static readonly PropSet Bell = new("铃", "#FFF4E8", "#D87A2C");
    public static readonly PropSet Spiral = new("晕", "#F8ECFF", "#9B62D8");
    public static readonly PropSet Letter = new("信", "#F2F7FF", "#4C79C8");
    public static readonly PropSet Cloud = new("云", "#F4F0FF", "#8265C9");
    public static readonly PropSet Cookie = new("饼", "#FFF3E4", "#B96A28");
    public static readonly PropSet Flag = new("旗", "#EAF8F2", "#2F8D63");
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
