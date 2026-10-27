param(
    [Parameter(Mandatory = $true)][ValidateSet('GetCaptcha', 'Login', 'SaveSession')][string]$Operation,
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$SessionPath,
    [Parameter(Mandatory = $true)][string]$ImagePath,
    [string]$DataDirectory,
    [int]$TargetHeight = 0
)

$ErrorActionPreference = 'Stop'
$saveSessionStage = 'script initialization'

function Get-SessionId([System.Net.HttpWebResponse]$Response) {
    foreach ($header in $Response.Headers.GetValues('Set-Cookie')) {
        $match = [regex]::Match($header, '(?i)JSESSIONID=([^;,\s]+)')
        if ($match.Success) { return $match.Groups[1].Value }
    }
    return $null
}

function Merge-ResponseCookies([string]$CookieHeader, [System.Net.HttpWebResponse]$Response) {
    $cookies = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($pair in ($CookieHeader -split ';')) {
        $separator = $pair.IndexOf('=')
        if ($separator -gt 0) {
            $cookies[$pair.Substring(0, $separator).Trim()] = $pair.Substring($separator + 1).Trim()
        }
    }
    foreach ($header in $Response.Headers.GetValues('Set-Cookie')) {
        $pair = ($header -split ';', 2)[0].Trim()
        $separator = $pair.IndexOf('=')
        if ($separator -gt 0) {
            $cookies[$pair.Substring(0, $separator).Trim()] = $pair.Substring($separator + 1).Trim()
        }
    }
    return (@($cookies.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join '; ')
}

function New-WebRequest(
    [string]$Url,
    [string]$Method,
    [string]$SessionId,
    [string]$CookieHeader = '',
    [string]$Referer = '',
    [int]$TimeoutMilliseconds = 15000) {
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Method = $Method
    # Follow redirects explicitly so every Set-Cookie rotation is captured before
    # the next request. Otherwise the captcha image and login POST can use different
    # JSESSIONIDs when an intermediate response changes the session.
    $request.AllowAutoRedirect = $false
    $request.CachePolicy = [System.Net.Cache.RequestCachePolicy]::new(
        [System.Net.Cache.RequestCacheLevel]::NoCacheNoStore)
    $request.Timeout = $TimeoutMilliseconds
    $request.ReadWriteTimeout = $TimeoutMilliseconds
    $request.UserAgent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) YuWangCloudSync-Setup'
    if ($Referer) { $request.Referer = $Referer }
    if (-not $CookieHeader -and $SessionId) { $CookieHeader = 'JSESSIONID=' + $SessionId }
    if ($CookieHeader) { $request.Headers['Cookie'] = $CookieHeader }
    return $request
}

function Invoke-RequestWithSession(
    [string]$Url,
    [string]$Method,
    [string]$SessionId,
    [string]$CookieHeader = '',
    [string]$Referer = '',
    [byte[]]$BodyBytes = $null,
    [string]$ContentType = '',
    [int]$TimeoutMilliseconds = 15000) {
    $currentUri = [Uri]$Url
    $currentMethod = $Method
    $currentBody = $BodyBytes

    for ($redirectCount = 0; $redirectCount -lt 8; $redirectCount++) {
        $request = New-WebRequest -Url $currentUri.AbsoluteUri -Method $currentMethod `
            -SessionId $SessionId -CookieHeader $CookieHeader -Referer $Referer `
            -TimeoutMilliseconds $TimeoutMilliseconds
        if ($currentBody -and $currentBody.Length -gt 0) {
            $request.ContentType = $ContentType
            $request.ContentLength = $currentBody.Length
            $requestStream = $request.GetRequestStream()
            try { $requestStream.Write($currentBody, 0, $currentBody.Length) }
            finally { $requestStream.Dispose() }
        }

        $httpResponse = $null
        try {
            $httpResponse = [System.Net.HttpWebResponse]$request.GetResponse()
        }
        catch [System.Net.WebException] {
            $httpResponse = [System.Net.HttpWebResponse]$_.Exception.Response
            if (-not $httpResponse) { throw }
        }

        $CookieHeader = Merge-ResponseCookies $CookieHeader $httpResponse
        $rotatedSessionId = Get-SessionId $httpResponse
        if ($rotatedSessionId) { $SessionId = $rotatedSessionId }

        $statusCode = [int]$httpResponse.StatusCode
        if ($statusCode -notin @(301, 302, 303, 307, 308)) {
            return [PSCustomObject]@{
                Response = $httpResponse
                SessionId = $SessionId
                CookieHeader = $CookieHeader
            }
        }

        $location = $httpResponse.Headers['Location']
        if ([string]::IsNullOrWhiteSpace($location)) {
            $httpResponse.Dispose()
            throw 'Redirect response did not include a Location header.'
        }
        $redirectUri = [Uri]::new($currentUri, $location)
        if (-not [string]::Equals($currentUri.Host, $redirectUri.Host, [StringComparison]::OrdinalIgnoreCase) -or
            ($currentUri.Scheme -eq 'https' -and $redirectUri.Scheme -ne 'https')) {
            $httpResponse.Dispose()
            throw 'Refusing to forward the login session to a different or insecure host.'
        }

        $httpResponse.Dispose()
        if ($statusCode -eq 303 -or
            (($statusCode -eq 301 -or $statusCode -eq 302) -and $currentMethod -eq 'POST')) {
            $currentMethod = 'GET'
            $currentBody = $null
        }
        $currentUri = $redirectUri
    }

    throw 'Too many redirects while accessing the login server.'
}

function Invoke-Get(
    [string]$Url,
    [string]$SessionId,
    [string]$CookieHeader = '',
    [int]$TimeoutMilliseconds = 15000) {
    $requestResult = Invoke-RequestWithSession -Url $Url -Method 'GET' -SessionId $SessionId `
        -CookieHeader $CookieHeader -TimeoutMilliseconds $TimeoutMilliseconds
    $response = $requestResult.Response
    try {
        if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300) {
            throw 'Unexpected HTTP response.'
        }
        $stream = $response.GetResponseStream()
        try { $stream.CopyTo([System.IO.Stream]::Null) }
        finally { $stream.Dispose() }
        return [PSCustomObject]@{
            SessionId = $requestResult.SessionId
            CookieHeader = $requestResult.CookieHeader
        }
    }
    finally {
        $response.Dispose()
    }
}

function ConvertTo-LoginCipher([string]$Value) {
    $modulusHex = '861ef1da51e3f4148c16adee8931c6cef71e12701395bec1185bad621230ec7ced81d7d7072de116c81fa1bf46891745d941fc933684fbfda45834fb4702cc44e1bc0dc4cec146efd950fa02b87856427adf8056b24d40702e6d07f488242172e0fa37023f14b7337bf73f90cad3510f835dc168ab903e146d00286986193f9f'
    $modulus = [System.Numerics.BigInteger]::Parse('0' + $modulusHex, [System.Globalization.NumberStyles]::HexNumber)
    $exponent = [System.Numerics.BigInteger]::Parse('010001', [System.Globalization.NumberStyles]::HexNumber)
    $characters = $Value.ToCharArray()
    if ($characters.Length -eq 0) { return '' }

    $parts = [System.Collections.Generic.List[string]]::new()
    for ($start = 0; $start -lt $characters.Length; $start += 126) {
        $chunk = [byte[]]::new(127)
        $count = [Math]::Min(126, $characters.Length - $start)
        for ($index = 0; $index -lt $count; $index++) {
            $chunk[$index] = [byte]([int]$characters[$start + $index] -band 0xff)
        }
        # BigInteger(byte[]) is signed little endian; the trailing zero keeps this value positive.
        $number = [System.Numerics.BigInteger]::new($chunk)
        $cipher = [System.Numerics.BigInteger]::ModPow($number, $exponent, $modulus)
        $hex = $cipher.ToString('x').TrimStart('0')
        if ($hex.Length -eq 0) { $hex = '0' }
        if ($hex.Length % 2 -ne 0) { $hex = '0' + $hex }
        $parts.Add($hex)
    }
    return [string]::Join(' ', $parts)
}

$response = $null
$image = $null
$imageStream = $null
$captchaBitmap = $null
$captchaGraphics = $null
$captchaFailureCode = 27
$loginFailureCode = 1
try {
    $inputLines = [System.IO.File]::ReadAllLines($InputPath, [System.Text.Encoding]::Unicode)
    Remove-Item -LiteralPath $InputPath -Force

    if ($Operation -eq 'GetCaptcha') {
        $captchaFailureCode = 24
        if ($TargetHeight -le 0) { exit 27 }
        # Windows PowerShell -NoProfile does not load the image decoder automatically.
        Add-Type -AssemblyName System.Drawing
        if ($inputLines.Length -lt 1 -or [string]::IsNullOrWhiteSpace($inputLines[0])) { exit 27 }
        $script:Server = $inputLines[0].TrimEnd('/')
        if (-not [Uri]::IsWellFormedUriString($script:Server, [UriKind]::Absolute)) { exit 27 }

        Remove-Item -LiteralPath $SessionPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $ImagePath -Force -ErrorAction SilentlyContinue
        $candidates = [System.Collections.Generic.List[string]]::new()
        $candidates.Add($script:Server)
        $authority = [regex]::Match($script:Server, '^https?://([^/?#]+)').Groups[1].Value
        if ($authority -and $authority -notmatch '^(?:\[[^\]]+\]|[^:]+):\d+$') {
            $alternate = [System.UriBuilder]::new($script:Server)
            $alternate.Scheme = 'http'
            $alternate.Port = 90
            $candidates.Add($alternate.Uri.AbsoluteUri.TrimEnd('/'))
        }

        for ($candidateIndex = 0; $candidateIndex -lt $candidates.Count; $candidateIndex++) {
            $script:Server = $candidates[$candidateIndex]
            # Keep automatic discovery brief; an explicitly supplied port keeps the normal timeout.
            $requestTimeout = 15000
            if ($candidateIndex -lt $candidates.Count - 1) { $requestTimeout = 3000 }
            try {
                $captchaFailureCode = 28
                $welcomeRequest = Invoke-Get ($script:Server + '/welcome.jsx') '' '' $requestTimeout
                $sessionId = $welcomeRequest.SessionId
                $cookieHeader = $welcomeRequest.CookieHeader

                $captchaFailureCode = 29
                $captchaUrl = $script:Server + '/servlet/VerificationCode?ram=' + [Guid]::NewGuid().ToString('N')
                $captchaRequest = Invoke-RequestWithSession -Url $captchaUrl -Method 'GET' `
                    -SessionId $sessionId -CookieHeader $cookieHeader -TimeoutMilliseconds $requestTimeout
                $response = $captchaRequest.Response
                $sessionId = $captchaRequest.SessionId
                $cookieHeader = $captchaRequest.CookieHeader
                $captchaFailureCode = 23
                if ([int]$response.StatusCode -ne 200) { throw 'Unexpected captcha HTTP response.' }
                $captchaFailureCode = 26
                if (-not $sessionId) { throw 'Missing login session.' }

                $captchaFailureCode = 25
                $imageStream = [System.IO.MemoryStream]::new()
                $response.GetResponseStream().CopyTo($imageStream)
                if ($imageStream.Length -lt 32) { throw 'Invalid captcha image.' }
                $imageStream.Position = 0
                $image = [System.Drawing.Image]::FromStream($imageStream)
                $captchaFailureCode = 24
                # Use the bitmap control's current client height, measured in device pixels
                # by NSIS, so the image matches the active DPI-scaled layout.
                $scale = [double]$TargetHeight / [double]$image.Height
                $width = [Math]::Max(1, [int][Math]::Round($image.Width * $scale))
                $height = $TargetHeight
                $captchaBitmap = [System.Drawing.Bitmap]::new(
                    $width, $height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
                $captchaGraphics = [System.Drawing.Graphics]::FromImage($captchaBitmap)
                $captchaGraphics.Clear([System.Drawing.Color]::White)
                $captchaGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $captchaGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $captchaGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $captchaGraphics.DrawImage($image, 0, 0, $width, $height)
                $captchaBitmap.Save($ImagePath, [System.Drawing.Imaging.ImageFormat]::Bmp)

                [System.IO.File]::WriteAllLines(
                    $SessionPath,
                    [string[]]@($script:Server, $sessionId, $cookieHeader),
                    [System.Text.Encoding]::Unicode)
                exit 0
            }
            catch {
                if ($captchaFailureCode -eq 24 -or $candidateIndex -eq $candidates.Count - 1) { throw }
            }
            finally {
                if ($captchaGraphics) { $captchaGraphics.Dispose(); $captchaGraphics = $null }
                if ($captchaBitmap) { $captchaBitmap.Dispose(); $captchaBitmap = $null }
                if ($image) { $image.Dispose(); $image = $null }
                if ($imageStream) { $imageStream.Dispose(); $imageStream = $null }
                if ($response) { $response.Dispose(); $response = $null }
            }
        }
    }

    if ($Operation -eq 'SaveSession') {
        $saveSessionStage = 'read data directory from helper input'
        if ([string]::IsNullOrWhiteSpace($DataDirectory) -and $inputLines.Length -ge 2) {
            $DataDirectory = $inputLines[1]
        }
        $saveSessionStage = 'validate helper input and data directory'
        if ($inputLines.Length -lt 1 -or [string]::IsNullOrWhiteSpace($inputLines[0]) -or
            [string]::IsNullOrWhiteSpace($DataDirectory) -or -not [System.IO.Path]::IsPathRooted($DataDirectory)) {
            [Console]::Out.WriteLine('SaveSession validation failed: username or data directory is missing.')
            exit 41
        }
        $saveSessionStage = 'read login session file'
        $sessionLines = [System.IO.File]::ReadAllLines($SessionPath, [System.Text.Encoding]::Unicode)
        if ($sessionLines.Length -lt 2 -or -not $sessionLines[1] -or
            -not [Uri]::IsWellFormedUriString($sessionLines[0], [UriKind]::Absolute)) {
            [Console]::Out.WriteLine('SaveSession validation failed: login session file is missing or invalid.')
            exit 42
        }
        $usernameKey = $inputLines[0].Trim().ToLowerInvariant()
        if (-not $usernameKey) {
            [Console]::Out.WriteLine('SaveSession validation failed: username is empty.')
            exit 43
        }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $usernameBytes = [System.Text.Encoding]::UTF8.GetBytes($usernameKey)
            $accountHash = [BitConverter]::ToString($sha.ComputeHash($usernameBytes)).Replace('-', '').Substring(0, 20)
        }
        finally {
            $sha.Dispose()
        }
        $serverUri = [Uri]$sessionLines[0]
        $cookieObject = [ordered]@{
            name = 'JSESSIONID'
            value = $sessionLines[1]
            domain = $serverUri.Host
            path = '/'
        }
        $cookieJson = '[' + (ConvertTo-Json -InputObject $cookieObject -Compress) + ']'
        $saveSessionStage = 'create data directory'
        [System.IO.Directory]::CreateDirectory($DataDirectory) | Out-Null
        $saveSessionStage = 'construct cookie paths'
        $cookiePath = [System.IO.Path]::Combine($DataDirectory, 'cookies-' + $accountHash + '.json')
        $temporaryCookiePath = $cookiePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        $backupCookiePath = [System.IO.Path]::Combine(
            $DataDirectory, 'cookies-backup-' + [Guid]::NewGuid().ToString('N') + '.json')
        try {
            $saveSessionStage = 'write temporary cookie file'
            [System.IO.File]::WriteAllText($temporaryCookiePath, $cookieJson, [System.Text.UTF8Encoding]::new($false))
            if ([System.IO.File]::Exists($cookiePath)) {
                $saveSessionStage = 'replace existing cookie file'
                [System.IO.File]::Replace($temporaryCookiePath, $cookiePath, $backupCookiePath)
                $saveSessionStage = 'remove previous cookie backup'
                [System.IO.File]::Delete($backupCookiePath)
                if ([System.IO.File]::Exists($backupCookiePath)) {
                    throw 'Previous cookie backup file could not be removed.'
                }
            }
            else {
                $saveSessionStage = 'move new cookie file into place'
                [System.IO.File]::Move($temporaryCookiePath, $cookiePath)
            }
        }
        finally {
            Remove-Item -LiteralPath $temporaryCookiePath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $backupCookiePath -Force -ErrorAction SilentlyContinue
        }
        exit 0
    }

    if ($inputLines.Length -lt 4) { exit 1 }
    $server = $inputLines[0].TrimEnd('/')
    $username = $inputLines[1]
    $password = $inputLines[2]
    $captcha = $inputLines[3]
    $sessionLines = [System.IO.File]::ReadAllLines($SessionPath, [System.Text.Encoding]::Unicode)
    if ($sessionLines.Length -lt 2 -or
        -not [string]::Equals($sessionLines[0], $server, [StringComparison]::OrdinalIgnoreCase)) {
        exit 1
    }
    $sessionId = $sessionLines[1]
    if (-not $sessionId) { exit 1 }
    $cookieHeader = if ($sessionLines.Length -ge 3 -and $sessionLines[2]) {
        $sessionLines[2]
    }
    else {
        'JSESSIONID=' + $sessionId
    }

    # Match Python urllib.parse.quote's default safe set, which leaves '/' unescaped.
    $quotedUsername = [Uri]::EscapeDataString($username).Replace('%2F', '/')
    $encryptedUsername = ConvertTo-LoginCipher $quotedUsername
    $encryptedPassword = ConvertTo-LoginCipher $password
    $body = 'guest=0&encryptloginname=' + [Uri]::EscapeDataString($encryptedUsername) +
        '&encryptpassword=' + [Uri]::EscapeDataString($encryptedPassword) +
        '&validate=' + [Uri]::EscapeDataString($captcha)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    $script:Server = $server
    $loginFailureCode = 35
    $loginRequest = Invoke-RequestWithSession -Url (
        $server + '/main.jsx?ram=' + [Guid]::NewGuid().ToString('N')) -Method 'POST' `
        -SessionId $sessionId -CookieHeader $cookieHeader -Referer ($server + '/welcome.jsx') `
        -BodyBytes $bytes -ContentType 'application/x-www-form-urlencoded' -TimeoutMilliseconds 15000
    $response = $loginRequest.Response
    $sessionId = $loginRequest.SessionId
    $cookieHeader = $loginRequest.CookieHeader
    if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300) {
        $loginFailureCode = 33
        throw 'Unexpected login HTTP response.'
    }
    $reader = [System.IO.StreamReader]::new($response.GetResponseStream(), [System.Text.Encoding]::UTF8)
    try { $html = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    $loginUrl = $response.ResponseUri.AbsoluteUri
    if ($loginUrl -match '(?i)welcome' -or $html.Contains('请输入登录名') -or
        [regex]::IsMatch($html, 'layer\.alert\(\s*"验证码错误')) {
        exit 1
    }
    $loginFailureCode = 34
    [System.IO.File]::WriteAllLines(
        $SessionPath,
        [string[]]@($server, $sessionId, $cookieHeader),
        [System.Text.Encoding]::Unicode)
    exit 0
}
catch {
    if ($Operation -eq 'GetCaptcha') {
        $failure = $_.Exception
        while ($failure.InnerException -and $failure -isnot [System.Net.WebException]) {
            $failure = $failure.InnerException
        }
        if ($failure -is [System.Net.WebException]) {
            switch ($failure.Status) {
                'Timeout' { exit 20 }
                'ConnectFailure' { exit 21 }
                'NameResolutionFailure' { exit 21 }
                'ProxyNameResolutionFailure' { exit 21 }
                'TrustFailure' { exit 22 }
                'SecureChannelFailure' { exit 22 }
                'ProtocolError' { exit 23 }
            }
        }
        exit $captchaFailureCode
    }
    if ($Operation -eq 'Login') {
        $failure = $_.Exception
        while ($failure.InnerException -and $failure -isnot [System.Net.WebException]) {
            $failure = $failure.InnerException
        }
        if ($failure -is [System.Net.WebException]) {
            switch ($failure.Status) {
                'Timeout' { exit 30 }
                'ConnectFailure' { exit 31 }
                'NameResolutionFailure' { exit 31 }
                'ProxyNameResolutionFailure' { exit 31 }
                'TrustFailure' { exit 32 }
                'SecureChannelFailure' { exit 32 }
                'ProtocolError' { exit 33 }
            }
        }
        exit $loginFailureCode
    }
    if ($Operation -eq 'SaveSession') {
        $failure = $_.Exception
        while ($failure.InnerException) { $failure = $failure.InnerException }
        $message = ($failure.Message -replace '[\r\n]+', ' ').Trim()
        $diagnosticDataDirectory = ($DataDirectory -replace '[\r\n\t]', '?')
        [Console]::Out.WriteLine(('SaveSession stage={0}; dataDirectory=[{1}]; exception={2}, HRESULT 0x{3:X8}: {4}' -f
            $saveSessionStage, $diagnosticDataDirectory, $failure.GetType().FullName,
            [int]$failure.HResult, $message))
        exit 40
    }
    exit 1
}
finally {
    if ($captchaGraphics) { $captchaGraphics.Dispose() }
    if ($captchaBitmap) { $captchaBitmap.Dispose() }
    if ($image) { $image.Dispose() }
    if ($imageStream) { $imageStream.Dispose() }
    if ($response) { $response.Dispose() }
    Remove-Item -LiteralPath $InputPath -Force -ErrorAction SilentlyContinue
}
