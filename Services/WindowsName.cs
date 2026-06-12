// mac 소스 대응: Sources/XFinder/Services/WindowsName.swift — Windows 호환 파일명 정리 (포팅에서는 'mac에서 온 호환 불가 이름 정리' + 사용자 입력 검증 용도 유지)
using System.Text;

namespace XFinder.Services;

/// <summary>
/// Windows 호환 파일명 정리 — 생성·이름 변경·복사 시 사전 검증 + 자동 정리.
/// 규칙·치환 결과는 mac 원본과 동일하게 유지 (크로스 플랫폼 동작 일관성, 스펙 §8).
/// </summary>
public static class WindowsName
{
    /// <summary>예약 장치 이름 — 원본 목록 그대로 (COM0/CONIN$ 등은 추가하지 않음).</summary>
    private static readonly HashSet<string> Reserved = BuildReserved();

    /// <summary>
    /// 스펙 §8.1 규칙 그대로:
    /// ① 제어 문자(&lt;0x20) 제거 ② 금지 문자 9종(&lt; &gt; : " / \ | ? *) → '_'
    /// ③ 끝의 공백·마침표 제거 ④ 예약 장치 이름(CON, PRN, AUX, NUL, COM1~9, LPT1~9)이면 앞에 '_'
    /// ⑤ 빈 결과 → "_" ⑥ NFC 정규화(한글 자소 분리 방지).
    /// </summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch < ' ') continue;                       // ① 제어 문자 제거
            sb.Append(IsForbidden(ch) ? '_' : ch);             // ② 금지 문자 치환
        }

        // ③ 끝의 공백·마침표 제거 (Windows 금지)
        int end = sb.Length;
        while (end > 0 && (sb[end - 1] == ' ' || sb[end - 1] == '.')) end--;
        sb.Length = end;
        var result = sb.ToString();

        // ④ 첫 마침표 앞 부분(stem)을 대문자로 비교 — 해당하면 앞에 '_' 부착 (NUL.txt → _NUL.txt)
        int dot = result.IndexOf('.');
        var stem = (dot < 0 ? result : result[..dot]).ToUpperInvariant();
        if (Reserved.Contains(stem)) result = "_" + result;

        if (result.Length == 0) result = "_";                  // ⑤ 빈 결과

        // ⑥ NFC 정규화 — 잘못된 서로게이트 등으로 실패하면 그대로 둔다.
        try { result = result.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { }
        return result;
    }

    /// <summary>정리가 필요한 이름인지 (Sanitize 결과가 원본과 다르면 true).</summary>
    public static bool NeedsSanitizing(string name) => Sanitize(name) != name;

    private static bool IsForbidden(char c)
        => c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

    private static HashSet<string> BuildReserved()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { "CON", "PRN", "AUX", "NUL" };
        for (int i = 1; i <= 9; i++)
        {
            set.Add("COM" + i);
            set.Add("LPT" + i);
        }
        return set;
    }
}
