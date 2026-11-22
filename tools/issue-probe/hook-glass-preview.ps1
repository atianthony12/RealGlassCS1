param([string]$File)
$t = [IO.File]::ReadAllText($File)
$nl = if ($t.Contains("`r`n")) { "`r`n" } else { "`n" }
$old = @'
                screenOk = s.Screen.Update(s.Camera, s.Util, p.Finished, _hizOff ? 0 : q.HiZLevels);
                GainAndGrade(s);
                DrawCity(s, drawGlass);
                s.Camera.Render();
            }
            pixels = ReadPixels(s);
            ReadGain(s);
        }
'@
$new = @'
                // ISSUE PROBE (tools/issue-probe): RG_DR=1, Dynamic Resolution: the glass's per-frame code
                // runs with no target on the camera (it sees the screen's size), the camera then renders
                // into the preview's target (screen x factor).
                bool issueDr = Env("RG_DR", "0") == "1";
                if (issueDr)
                    s.Camera.targetTexture = null;
                screenOk = s.Screen.Update(s.Camera, s.Util, p.Finished, _hizOff ? 0 : q.HiZLevels);
                if (issueDr)
                    s.Camera.targetTexture = s.Target;
                GainAndGrade(s);
                DrawCity(s, drawGlass);
                s.Camera.Render();
            }
            pixels = ReadPixels(s);
            ReadGain(s);
            if (_debug > 14.5f && _debug < 15.5f)
            {
                int issueRed = 0, issueGreen = 0, issueYellow = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color c = pixels[i];
                    if (c.r > 0.5f && c.g < 0.3f * c.r && c.b < 0.3f * c.r) issueRed++;
                    else if (c.r > 0.5f && c.g > 0.5f && c.b < 0.3f * c.g) issueYellow++;
                    else if (c.g > 0.5f && c.r < 0.3f * c.g && c.b < 0.5f * c.g) issueGreen++;
                }
                _gainInfo += " | ISSUE dr " + Env("RG_DR", "0") + " render " + s.Width + "x" + s.Height + " copies " + s.Screen.Width + "x" +
                             s.Screen.Height + " own " + issueGreen + " neighbours " + issueYellow + " none " + issueRed;
            }
        }
'@
$o = $old.Replace("`r`n", "`n").Replace("`n", $nl); $n = $new.Replace("`r`n", "`n").Replace("`n", $nl)
if (-not $t.Contains($o)) { throw "hook point not found in $File" }
[IO.File]::WriteAllText($File, $t.Replace($o, $n))
"hooked $File"
