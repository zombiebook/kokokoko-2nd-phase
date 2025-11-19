using System;
using System.Reflection;
using UnityEngine;

namespace kokorevenge
{
    // Duckov Mods 로더 엔트리: kokorevenge.ModBehaviour
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                GameObject go = new GameObject("KokoRevengeRoot");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<KokoRevengeManager>();
                Debug.Log("[KokoRevenge] OnAfterSetup - KokoRevengeManager 초기화 완료");
            }
            catch (Exception ex)
            {
                Debug.Log("[KokoRevenge] OnAfterSetup 예외: " + ex);
            }
        }
    }

    /// <summary>
    /// - 씬 전체 Health를 주기적으로 스캔
    /// - MaxHP >= MinBossHpThreshold 인 애들만 "보스 후보"로 보고,
    ///   그중 MaxHP가 가장 큰 놈 1명을 코코 보스로 간주.
    ///
    /// - 코코 HP가 "원래 MaxHP의 30% 이하가 되는 순간":
    ///     · 2페이즈 돌입
    ///     · "대가를 치르게 해주마" 3초간 표시 (텍스트 맥동)
    ///     · HP를 원래 MaxHP까지 즉시 풀회복 시도
    ///     · 이후 1초 동안 매 프레임 HP를 다시 Max까지 끌어올리며 재시도
    ///
    /// - 2페이즈 이후:
    ///     · 들어오는 대미지를 절반만 먹이도록 HP를 보정해서
    ///       → 실제로는 2배 오래 버티게 만든다 (실질 피통 2배).
    /// </summary>
    internal class KokoRevengeManager : MonoBehaviour
    {
        private class TrackedBoss
        {
            public Health health;
            public float originalMaxHp;
            public float lastHp;
            public bool phase2Triggered;
            public bool haveInitialHp;
        }

        private TrackedBoss _boss;

        private float _nextScanTime;
        private const float ScanInterval = 1.0f;

        // 이 값 이상 MaxHP 가진 Health만 "보스 후보"로 본다.
        private const float MinBossHpThreshold = 200f;

        // 2페이즈 발동 기준: MaxHP 의 30%
        private const float Phase2HpPercent = 0.3f;

        private const string TauntText = "대가를 치르게 해주마";

        // HUD + 맥동 효과
        private float _tauntUntilTime;
        private GUIStyle _tauntStyle;
        private bool _onGuiLoggedOnce;
        private int _baseTauntFontSize = 32;
        private float _pulseSpeed = 4f;     // 맥동 속도
        private float _pulseScale = 0.15f;  // 폰트 크기 변화 비율 (15%)

        // 2페이즈 진입 직후 "강제 회복" 유지 시간
        private float _extraHealUntilTime;

        // Health 리플렉션
        private static bool _healthReflectionInitialized;
        private static FieldInfo _maxHpField;
        private static FieldInfo _curHpField;
        private static PropertyInfo _maxHpProperty;
        private static PropertyInfo _curHpProperty;
        private static MethodInfo _healMethod;      // Heal(float) / AddHealth(float) / AddHP(float) 류

        private void Awake()
        {
            Debug.Log("[KokoRevenge] Awake - 매니저 초기화");
        }

        private void Update()
        {
            // 아직 2페이즈 전이면 주기적으로 "보스 후보" 스캔
            if (!_bossPhase2Done && Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + ScanInterval;
                TrySelectBossByHpThreshold();
            }

            if (_boss != null && _boss.health != null)
            {
                UpdateBossPhase2AndDamageScaling();
            }
        }

        private bool _bossPhase2Done
        {
            get { return _boss != null && _boss.phase2Triggered; }
        }

        private void OnGUI()
        {
            if (Time.time >= _tauntUntilTime) return;

            if (!_onGuiLoggedOnce)
            {
                _onGuiLoggedOnce = true;
                Debug.Log("[KokoRevenge] OnGUI 활성 - HUD 그리기 시작");
            }

            EnsureTauntStyle();

            // ==== 맥동(펄스) 효과: 폰트 크기를 시간에 따라 살짝 키웠다 줄였다 ====
            float t = Time.time * _pulseSpeed;
            float scale = 1f + _pulseScale * Mathf.Sin(t); // 1 ± 0.15
            int dynamicFontSize = Mathf.Max(10, Mathf.RoundToInt(_baseTauntFontSize * scale));
            _tauntStyle.fontSize = dynamicFontSize;

            GUIContent content = new GUIContent(TauntText);
            Vector2 size = _tauntStyle.CalcSize(content);

            float boxWidth = size.x + 40f;
            float boxHeight = size.y + 30f;

            float x = (Screen.width - boxWidth) * 0.5f;
            float y = Screen.height * 0.4f;

            Rect boxRect = new Rect(x, y, boxWidth, boxHeight);
            Rect labelRect = new Rect(
                x + (boxWidth - size.x) * 0.5f,
                y + (boxHeight - size.y) * 0.5f,
                size.x,
                size.y
            );

            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.Box(boxRect, GUIContent.none);
            GUI.color = old;

            GUI.Label(labelRect, content, _tauntStyle);
        }

        // =============== HP 기준 보스 선택 ==================

        private void TrySelectBossByHpThreshold()
        {
            try
            {
                Health[] all = UnityEngine.Object.FindObjectsOfType<Health>();
                if (all == null || all.Length == 0)
                {
                    Debug.Log("[KokoRevenge] TrySelectBossByHpThreshold - Health 없음");
                    return;
                }

                Health best = null;
                float bestMax = 0f;

                for (int i = 0; i < all.Length; i++)
                {
                    Health h = all[i];
                    if (h == null) continue;

                    float max = GetMaxHp(h);
                    if (max < MinBossHpThreshold) continue;  // 작은 피통은 전부 스킵

                    if (max > bestMax)
                    {
                        bestMax = max;
                        best = h;
                    }
                }

                if (best == null || bestMax <= 0f)
                {
                    // 아직 코코코코가 스폰 안 된 상태일 수도 있으니 조용히 리턴
                    return;
                }

                if (_boss == null || _boss.health == null)
                {
                    float cur = GetCurrentHp(best);
                    _boss = new TrackedBoss
                    {
                        health = best,
                        originalMaxHp = bestMax,
                        lastHp = cur,
                        phase2Triggered = false,
                        haveInitialHp = false
                    };

                    Debug.Log("[KokoRevenge] 보스 최초 선택(HP 기준): maxHp=" +
                              bestMax + " curHp=" + cur + " obj=" + best.transform.name);
                    return;
                }

                // 이미 보스가 있는데, 더 큰 피통이 새로 생겼으면 갈아타기 (2페이즈 전일 때만)
                if (!_boss.phase2Triggered)
                {
                    float currentMax = GetMaxHp(_boss.health);
                    if (currentMax <= 0f) currentMax = _boss.originalMaxHp;

                    if (best != _boss.health && bestMax > currentMax * 1.5f + 1f)
                    {
                        float cur = GetCurrentHp(best);
                        Debug.Log("[KokoRevenge] 보스 재선택(HP 기준): oldMax=" +
                                  currentMax + " newMax=" + bestMax +
                                  " newCur=" + cur + " obj=" + best.transform.name);

                        _boss = new TrackedBoss
                        {
                            health = best,
                            originalMaxHp = bestMax,
                            lastHp = cur,
                            phase2Triggered = false,
                            haveInitialHp = false
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[KokoRevenge] TrySelectBossByHpThreshold 예외: " + ex);
            }
        }

        // =============== 2페이즈 + 대미지 보정 ==================

        private void UpdateBossPhase2AndDamageScaling()
        {
            try
            {
                float curHp = GetCurrentHp(_boss.health);

                // 초기 HP 한 번만 저장
                if (!_boss.haveInitialHp)
                {
                    _boss.originalMaxHp = GetMaxHp(_boss.health);
                    if (_boss.originalMaxHp <= 0f)
                        _boss.originalMaxHp = curHp > 0f ? curHp : 1f;

                    _boss.lastHp = curHp;
                    _boss.haveInitialHp = true;
                    return;
                }

                float prevHp = _boss.lastHp;

                // 이미 완전히 죽은 상태
                if (curHp <= 0f && prevHp <= 0f)
                {
                    _boss.lastHp = curHp;
                    return;
                }

                // =========== 0단계: 2페이즈 진입 직후 강제 회복 구간 ===========
                if (_boss.phase2Triggered && Time.time < _extraHealUntilTime)
                {
                    float baseMaxForHeal = _boss.originalMaxHp;
                    if (baseMaxForHeal <= 0f)
                        baseMaxForHeal = GetMaxHp(_boss.health);
                    if (baseMaxForHeal <= 0f)
                        baseMaxForHeal = 1f;

                    ForceHealTo(_boss.health, baseMaxForHeal);
                    _boss.lastHp = GetCurrentHp(_boss.health);

                    // 이 구간에서는 대미지 보정 안 하고 그대로 리턴 (거의 무적 + 회복 연출)
                    return;
                }

                // ================= 1단계: 2페이즈 진입 조건 =================
                if (!_boss.phase2Triggered)
                {
                    float baseMax = _boss.originalMaxHp;
                    if (baseMax <= 0f)
                        baseMax = GetMaxHp(_boss.health);
                    if (baseMax <= 0f)
                        baseMax = prevHp > 0f ? prevHp : (curHp > 0f ? curHp : 1f);

                    float threshold = baseMax * Phase2HpPercent;   // 30%
                    if (threshold < 1f) threshold = 1f;

                    // *** 단순화: 현재 HP가 30% 이하가 되는 순간이면 언제든 발동 ***
                    bool hpBelow30 = (curHp > 0f && curHp <= threshold);

                    if (hpBelow30)
                    {
                        _boss.phase2Triggered = true;

                        // === 2페이즈 진입과 동시에 HP 풀회복 시도 ===
                        float healTarget = baseMax;
                        if (healTarget < 1f) healTarget = 1f;
                        ForceHealTo(_boss.health, healTarget);
                        float afterHeal = GetCurrentHp(_boss.health);
                        _boss.lastHp = afterHeal;

                        // 1초 동안은 계속 강제 회복 시도
                        _extraHealUntilTime = Time.time + 1.0f;

                        _tauntUntilTime = Time.time + 3f;

                        Debug.Log("[KokoRevenge] 코코 2페이즈 진입(HP 30% 이하)! " +
                                  "baseMax=" + baseMax +
                                  " threshold(30%)=" + threshold +
                                  " prevHp=" + prevHp +
                                  " curHp(beforeHeal)=" + curHp +
                                  " curHp(afterHeal)=" + afterHeal);
                    }
                    else
                    {
                        _boss.lastHp = curHp;
                    }

                    return;
                }

                // ================= 2단계: 2페이즈 이후 - 들어오는 대미지 절반 처리 =================

                // 2페이즈 상태에서 HP가 줄어든 프레임만 처리
                if (prevHp > 0f && curHp > 0f && curHp < prevHp)
                {
                    float damage = prevHp - curHp;

                    // 2페이즈에서는 실제로는 절반만 깎이게
                    float effectiveDamage = damage * 0.5f;
                    float healBack = damage - effectiveDamage; // 되돌려줄 양
                    float newHp = curHp + healBack;

                    if (newHp < 1f) newHp = 1f;

                    SetCurrentHp(_boss.health, newHp);
                    _boss.lastHp = newHp;

                    Debug.Log("[KokoRevenge] 2페이즈 대미지 보정: " +
                              "rawDamage=" + damage +
                              " effectiveDamage=" + effectiveDamage +
                              " prevHp=" + prevHp +
                              " curHp(beforeFix)=" + curHp +
                              " curHp(afterFix)=" + newHp);
                }
                else
                {
                    _boss.lastHp = curHp;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[KokoRevenge] UpdateBossPhase2AndDamageScaling 예외: " + ex);
            }
        }

        // =============== Health 리플렉션 & HP 읽기/쓰기 ================

        private static void EnsureHealthReflection(Health sample)
        {
            if (_healthReflectionInitialized) return;
            if (sample == null) return;

            _healthReflectionInitialized = true;

            Type t = sample.GetType();

            _maxHpProperty = t.GetProperty(
                "MaxHealth",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _curHpProperty = t.GetProperty(
                "CurrentHealth",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                string name = f.Name.ToLowerInvariant();

                if (f.FieldType != typeof(float)) continue;

                if (_maxHpField == null &&
                    name.Contains("max") &&
                    (name.Contains("hp") || name.Contains("health")))
                {
                    _maxHpField = f;
                }
                else if (_curHpField == null &&
                         (name.Contains("cur") || name.Contains("current")) &&
                         (name.Contains("hp") || name.Contains("health")))
                {
                    _curHpField = f;
                }
            }

            // Heal / AddHealth / AddHP 류 메서드 탐색
            MethodInfo[] methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                string n = m.Name.ToLowerInvariant();

                if (_healMethod != null) break;

                if (n.Contains("heal") || n.Contains("addhealth") || n.Contains("addhp"))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(float))
                    {
                        _healMethod = m;
                        Debug.Log("[KokoRevenge] Heal 메서드 발견: " + m.Name);
                        break;
                    }
                }
            }

            Debug.Log("[KokoRevenge] Health 리플렉션 세팅 완료. " +
                      "maxField=" + (_maxHpField != null ? _maxHpField.Name : "null") +
                      " curField=" + (_curHpField != null ? _curHpField.Name : "null") +
                      " maxProp=" + (_maxHpProperty != null ? _maxHpProperty.Name : "null") +
                      " curProp=" + (_curHpProperty != null ? _curHpProperty.Name : "null") +
                      " healMethod=" + (_healMethod != null ? _healMethod.Name : "null"));
        }

        private static float GetMaxHp(Health h)
        {
            if (h == null) return 0f;
            EnsureHealthReflection(h);

            try
            {
                if (_maxHpProperty != null && _maxHpProperty.CanRead)
                {
                    object v = _maxHpProperty.GetValue(h, null);
                    if (v is float f) return f;
                }

                if (_maxHpField != null)
                {
                    object v = _maxHpField.GetValue(h);
                    if (v is float f) return f;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[KokoRevenge] GetMaxHp 예외: " + ex);
            }

            return 0f;
        }

        private static float GetCurrentHp(Health h)
        {
            if (h == null) return 0f;
            EnsureHealthReflection(h);

            try
            {
                if (_curHpProperty != null && _curHpProperty.CanRead)
                {
                    object v = _curHpProperty.GetValue(h, null);
                    if (v is float f) return f;
                }

                if (_curHpField != null)
                {
                    object v = _curHpField.GetValue(h);
                    if (v is float f) return f;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[KokoRevenge] GetCurrentHp 예외: " + ex);
            }

            return 0f;
        }

        // Max는 안 건드리고, CurrentHP만 바꾸는 전용 함수
        private static void SetCurrentHp(Health h, float curHp)
        {
            if (h == null) return;
            EnsureHealthReflection(h);

            bool wroteCur = false;

            try
            {
                if (_curHpProperty != null)
                {
                    MethodInfo set = _curHpProperty.GetSetMethod(true);
                    if (set != null)
                    {
                        set.Invoke(h, new object[] { curHp });
                        wroteCur = true;
                    }
                }

                if (_curHpField != null)
                {
                    _curHpField.SetValue(h, curHp);
                    wroteCur = true;
                }

                float afterCur = GetCurrentHp(h);
                Debug.Log("[KokoRevenge] SetCurrentHp: requested=" + curHp +
                          " afterCur=" + afterCur +
                          " wroteCur=" + wroteCur);
            }
            catch (Exception ex)
            {
                Debug.Log("[KokoRevenge] SetCurrentHp 예외: " + ex);
            }
        }

        // 게임 안쪽 Heal 함수까지 포함해서 "targetHp까지 끌어올리기"
        private static void ForceHealTo(Health h, float targetHp)
        {
            if (h == null) return;
            EnsureHealthReflection(h);

            try
            {
                float cur = GetCurrentHp(h);
                if (cur >= targetHp) return;

                float need = targetHp - cur;

                bool usedHeal = false;

                // 1) Heal/ AddHealth / AddHP 같은 메서드 있으면 그걸로 시도
                if (_healMethod != null)
                {
                    try
                    {
                        _healMethod.Invoke(h, new object[] { need });
                        usedHeal = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.Log("[KokoRevenge] Heal 메서드 호출 예외: " + ex);
                    }
                }

                // 2) 그래도 부족하면 CurrentHP를 직접 target으로 고정
                SetCurrentHp(h, targetHp);

                float after = GetCurrentHp(h);
                Debug.Log("[KokoRevenge] ForceHealTo: target=" + targetHp +
                          " before=" + cur +
                          " after=" + after +
                          " usedHealMethod=" + usedHeal);
            }
            catch (Exception ex)
            {
                Debug.Log("[KokoRevenge] ForceHealTo 예외: " + ex);
            }
        }

        // =============== HUD 스타일 ==================

        private void EnsureTauntStyle()
        {
            if (_tauntStyle != null) return;

            _tauntStyle = new GUIStyle(GUI.skin.label);
            _tauntStyle.alignment = TextAnchor.MiddleCenter;
            _tauntStyle.fontSize = _baseTauntFontSize;
            _tauntStyle.fontStyle = FontStyle.Bold;
            _tauntStyle.normal.textColor = Color.red;
        }
    }
}
