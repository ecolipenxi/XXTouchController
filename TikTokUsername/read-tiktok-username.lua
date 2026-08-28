-- TikTok Username Reader 0.5
-- Safety rules:
--   * never wake a screen-off/locked device;
--   * Agent 2.3 screen-state is advisory; visible OCR confirms a lit screen;
--   * never stop another Lua task;
--   * every tap comes from screen.find_image();
--   * an unknown popup stops the script so Windows can save a snapshot.
-- @asset: tur-profile-tab.png
-- @asset: tur-profile-tab-ja-light.png
-- @asset: tur-profile-tab-en-light.png
-- @asset: tur-ask-not-track.png
-- @asset: tur-dont-allow.png
-- @asset: tur-find-contacts-dont-allow-en.png
-- @asset: tur-black-action-button.png
-- @asset: tur-contacts-deny-ja.png
-- @asset: tur-not-now.png
-- @asset: tur-photo-picker-close.png
-- @asset: tur-modal-close-x.png
-- @asset: tur-security-check-close-x.png

local IMAGE_ROOT = sys.root_dir() .. "/codetiktok/images/"
local BUNDLES = {
  "com.ss.iphone.ugc.Ame",
  "com.zhiliaoapp.musically",
  "com.zhiliaoapp.musically.go",
  "com.ss.iphone.ugc.Aweme",
}

local CFG = {
  launch_wait_ms = 9000,
  profile_wait_ms = 7000,
  minimum_profile_observe_seconds = 30,
  -- Recovery from Settings/Home can consume two launch waits. Normal devices
  -- still finish after the 30-second verification window; only recovery cases
  -- use this larger ceiling.
  maximum_profile_observe_seconds = 80,
  poll_ms = 1800,
  required_username_hits = 3,
}

local IMG = {
  profile = {
    name = "tur-profile-tab.png", similarity = 0.45,
    x = 610, y = 1180, w = 140, h = 154, dx = 56, dy = 65,
  },
  profile_ja_light = {
    -- Japanese profile tab on a light video. A high threshold and the tiny
    -- bottom-right search box prevent the uniform video background from being
    -- mistaken for the tab.
    name = "tur-profile-tab-ja-light.png", similarity = 0.98,
    x = 610, y = 1200, w = 140, h = 134, dx = 69, dy = 58,
  },
  profile_en_light = {
    -- English profile tab rendered over a very light video/background.
    name = "tur-profile-tab-en-light.png", similarity = 0.98,
    x = 610, y = 1200, w = 140, h = 134, dx = 69, dy = 58,
  },
  ask_not_track = {
    name = "tur-ask-not-track.png", similarity = 0.90,
    x = 70, y = 650, w = 610, h = 310, dx = 220, dy = 31,
  },
  dont_allow = {
    -- Search only the left action. A broad search could match the right-side
    -- OK/Open Settings action on some Find contacts dialog variants.
    name = "tur-dont-allow.png", similarity = 0.90,
    x = 70, y = 620, w = 300, h = 210, dx = 70, dy = 25,
  },
  contacts_deny_en = {
    -- Exact lowercase English variant captured from the in-app Find contacts
    -- sheet. Its search box is also confined to the left action.
    name = "tur-find-contacts-dont-allow-en.png", similarity = 0.96,
    x = 120, y = 760, w = 260, h = 130, dx = 80, dy = 28,
  },
  contacts_deny_open_settings_en = {
    -- A taller education sheet puts the same left action below an illustration
    -- and pairs it with "Open settings" on the right.
    name = "tur-find-contacts-dont-allow-en.png", similarity = 0.96,
    x = 120, y = 880, w = 260, h = 120, dx = 80, dy = 28,
  },
  black_action = {
    name = "tur-black-action-button.png", similarity = 0.96,
    x = 20, y = 1050, w = 710, h = 284, dx = 95, dy = 52,
  },
  contacts_deny_ja = {
    name = "tur-contacts-deny-ja.png", similarity = 0.95,
    x = 125, y = 890, w = 240, h = 90, dx = 90, dy = 22,
  },
  not_now = {
    name = "tur-not-now.png", similarity = 0.95,
    x = 140, y = 850, w = 230, h = 120, dx = 80, dy = 24,
  },
  photo_picker_close = {
    name = "tur-photo-picker-close.png", similarity = 0.95,
    x = 20, y = 50, w = 100, h = 100, dx = 20, dy = 20,
  },
  modal_close = {
    name = "tur-modal-close-x.png", similarity = 0.95,
    x = 650, y = 230, w = 90, h = 100, dx = 20, dy = 20,
  },
  security_check_close = {
    name = "tur-security-check-close-x.png", similarity = 0.97,
    x = 650, y = 500, w = 100, h = 130, dx = 29, dy = 29,
  },
}

local function wait(ms)
  local remaining = math.max(0, tonumber(ms) or 0)
  while remaining > 0 do
    local slice = math.min(remaining, 400)
    sys.msleep(slice)
    remaining = remaining - slice
  end
end

local function log(event, detail)
  local value = tostring(detail or ""):gsub("[\r\n]+", " / ")
  nLog("[TUR] EVENT|" .. tostring(event) .. "|" .. value)
end

local function normalize(value)
  return string.lower(tostring(value or ""))
    :gsub("[%s%p]", "")
    :gsub("â€™", "")
    :gsub("’", "")
    :gsub("‘", "")
end

local function contains(normalized, value)
  return normalized:find(normalize(value), 1, true) ~= nil
end

local function ocr(x, y, w, h)
  local ok, text, err = pcall(function()
    if x then return screen.ocr_text(x, y, w, h) end
    return screen.ocr_text()
  end)
  if not ok then
    log("OCR_ERROR", text)
    return ""
  end
  return tostring(text or err or "")
end

local function find_once(spec)
  local ok, x, y = pcall(function()
    return screen.find_image(
      IMAGE_ROOT .. spec.name,
      spec.similarity,
      spec.x, spec.y, spec.w, spec.h)
  end)
  if ok and x and x >= 0 and y and y >= 0 then return x, y end
  return nil, nil
end

local function find_for(spec, seconds)
  local deadline = os.time() + math.max(0, seconds or 0)
  repeat
    local x, y = find_once(spec)
    if x then return x, y end
    wait(300)
  until os.time() > deadline
  return nil, nil
end

local PROFILE_IMAGES = {
  IMG.profile,
  IMG.profile_ja_light,
  IMG.profile_en_light,
}

local function find_profile_for(seconds)
  local deadline = os.time() + math.max(0, seconds or 0)
  repeat
    for _, spec in ipairs(PROFILE_IMAGES) do
      local x, y = find_once(spec)
      if x then return x, y, spec end
    end
    wait(300)
  until os.time() > deadline
  return nil, nil, nil
end

local function tap_found(spec, x, y, label, after_ms)
  touch.tap(x + spec.dx, y + spec.dy)
  log("IMAGE_TAPPED", label .. "@" .. tostring(x) .. "," .. tostring(y))
  wait(after_ms or 1800)
end

local function open_tiktok()
  for _, bundle in ipairs(BUNDLES) do
    local ok, result = pcall(function() return app.run(bundle) end)
    if ok and result ~= false then
      log("APP_OPENED", bundle)
      return bundle
    end
  end
  return nil
end

local function reopen_tiktok(bundle)
  pcall(function() return app.kill(bundle) end)
  wait(900)
  local ok, result = pcall(function() return app.run(bundle) end)
  if ok and result ~= false then
    log("APP_REOPENED", bundle)
    return true
  end
  return false
end

local contacts_ja_taps = 0
local contacts_en_taps = 0
local generic_dont_allow_taps = 0
local not_now_taps = 0
local photo_picker_taps = 0
local modal_close_taps = 0
local security_check_taps = 0

local function handle_known_system_prompt_once()
  local raw = ocr()
  local text = normalize(raw)

  -- "Live Photos" remains recognizable even when the rest of the picker is
  -- Japanese; the X template and its top-left search box provide the second
  -- independent condition before a tap is allowed.
  local is_photo_picker = contains(text, "Live Photos")
  if photo_picker_taps < 1 and is_photo_picker then
    local x, y = find_for(IMG.photo_picker_close, 2)
    if not x then
      log("KNOWN_PROMPT_IMAGE_MISSING", "Photo picker close")
      return false, true
    end
    local fresh = normalize(ocr())
    if not contains(fresh, "Live Photos") then return true, false end
    photo_picker_taps = photo_picker_taps + 1
    tap_found(IMG.photo_picker_close, x, y, "Photo picker close", 2800)
    return true, true
  end

  -- Some accounts show a lower sheet titled "Let's do a quick security
  -- checkup" after Profile opens. Close only its exact top-right X and only
  -- while both identifying OCR phrases are still visible.
  local is_security_check = contains(text, "quick security checkup")
    and contains(text, "personalized security tips")
  if security_check_taps < 1 and is_security_check then
    local x, y = find_for(IMG.security_check_close, 2)
    if not x then
      log("KNOWN_PROMPT_IMAGE_MISSING", "Security check close")
      return false, true
    end
    local fresh = normalize(ocr())
    if not contains(fresh, "quick security checkup")
        or not contains(fresh, "personalized security tips") then
      return true, false
    end
    security_check_taps = security_check_taps + 1
    tap_found(IMG.security_check_close, x, y,
      "Security check close", 3000)
    return true, true
  end

  -- A TikTok information sheet can cover the profile on localized devices.
  -- Its X is confined to a small top-right region and the 95% template was
  -- verified negative on a normal Profile screen before enabling this tap.
  if modal_close_taps < 1 then
    local x, y = find_once(IMG.modal_close)
    if x then
      wait(180)
      local fresh_x, fresh_y = find_once(IMG.modal_close)
      if fresh_x then
        modal_close_taps = modal_close_taps + 1
        tap_found(IMG.modal_close, fresh_x, fresh_y, "TikTok modal close", 2800)
        return true, true
      end
    end
  end

  if not_now_taps < 1
      and contains(text, "Save login for next time")
      and contains(text, "Not now") then
    local x, y = find_for(IMG.not_now, 2)
    if not x then
      log("KNOWN_PROMPT_IMAGE_MISSING", "Save login / Not now")
      return false, true
    end
    local fresh = normalize(ocr())
    if not contains(fresh, "Save login for next time")
        or not contains(fresh, "Not now") then
      return true, false
    end
    not_now_taps = not_now_taps + 1
    tap_found(IMG.not_now, x, y, "Not now", 2800)
    return true, true
  end

  -- TikTok has an English Find contacts sheet whose right action may open
  -- iOS Settings. Require both the dialog text and a tight image match inside
  -- the left half before tapping Don't allow.
  local is_find_contacts = contains(text, "Find contacts")
    or (contains(text, "syncing your phone contacts")
      and contains(text, "get discovered"))
  if is_find_contacts
      and (contains(text, "Don't Allow") or contains(text, "Dont Allow")) then
    if contacts_en_taps >= 2 then
      log("POPUP_UNSUPPORTED",
        "Find contacts remained after two verified left-button taps")
      return false, true
    end
    local contacts_spec = contains(text, "Open settings")
      and IMG.contacts_deny_open_settings_en or IMG.contacts_deny_en
    local x, y = find_for(contacts_spec, 2)
    if not x then
      log("KNOWN_PROMPT_IMAGE_MISSING", "Find contacts / Don't allow")
      return false, true
    end
    local fresh = normalize(ocr())
    local fresh_is_find_contacts = contains(fresh, "Find contacts")
      or (contains(fresh, "syncing your phone contacts")
        and contains(fresh, "get discovered"))
    if not fresh_is_find_contacts
        or (not contains(fresh, "Don't Allow")
          and not contains(fresh, "Dont Allow")) then
      return true, false
    end
    contacts_en_taps = contacts_en_taps + 1
    tap_found(contacts_spec, x, y,
      "Find contacts / Don't allow (left)", 3000)
    return true, true
  end

  -- The Japanese contacts dialog is not reliably decoded by English OCR.
  -- Only test its tight text template when the profile header is obscured,
  -- and never tap this dialog more than once per run.
  if contacts_ja_taps < 1 then
    local header = ocr(20, 100, 710, 540)
    local header_has_username = header:match("@[A-Za-z0-9%._]+") ~= nil
    local clearly_other_screen = contains(text, "Live Photos")
      or contains(text, "Recents")
      or contains(text, "No photos or videos available")
      or contains(text, "Settings")
      or contains(text, "Siri")
      or contains(text, "For You")
    if not header_has_username and not clearly_other_screen then
      local x, y = find_once(IMG.contacts_deny_ja)
      if x then
        wait(180)
        local fresh_x, fresh_y = find_once(IMG.contacts_deny_ja)
        if fresh_x then
          contacts_ja_taps = contacts_ja_taps + 1
          tap_found(IMG.contacts_deny_ja, fresh_x, fresh_y,
            "Contacts deny (Japanese)", 2800)
          return true, true
        end
      end
    end
  end

  if contains(text, "Ask App Not to Track") then
    local x, y = find_for(IMG.ask_not_track, 2)
    if not x then
      log("KNOWN_PROMPT_IMAGE_MISSING", "Ask App Not to Track")
      return false, true
    end
    -- Re-read immediately before the tap; never trust an old template frame.
    if not contains(normalize(ocr()), "Ask App Not to Track") then return true, false end
    tap_found(IMG.ask_not_track, x, y, "Ask App Not to Track", 2600)
    return true, true
  end

  if not is_find_contacts
      and generic_dont_allow_taps < 2
      and (contains(text, "Don't Allow") or contains(text, "Dont Allow")) then
    local x, y = find_for(IMG.dont_allow, 2)
    if not x then
      log("KNOWN_PROMPT_IMAGE_MISSING", "Don't Allow")
      return false, true
    end
    local fresh = normalize(ocr())
    if not contains(fresh, "Don't Allow") and not contains(fresh, "Dont Allow") then
      return true, false
    end
    generic_dont_allow_taps = generic_dont_allow_taps + 1
    tap_found(IMG.dont_allow, x, y, "Don't Allow (left)", 2600)
    return true, true
  end

  if contains(text, "Agree and continue") then
    local x, y = find_for(IMG.black_action, 3)
    if not x then
      log("KNOWN_PROMPT_IMAGE_MISSING", "Agree and continue")
      return false, true
    end
    if not contains(normalize(ocr()), "Agree and continue") then return true, false end
    tap_found(IMG.black_action, x, y, "Agree and continue", 3000)
    return true, true
  end

  return true, false
end

local function parse_username(text)
  for line in tostring(text or ""):gmatch("[^\r\n]+") do
    local value = line:match("@([A-Za-z0-9%._]+)")
    if value and #value >= 2 and #value <= 32 then
      value = value:gsub("^[%.]+", ""):gsub("[%.]+$", "")
      if #value >= 2 then return "@" .. value end
    end
  end
  return nil
end

local function looks_like_profile(text)
  local has_all_counters = contains(text, "Following")
    and contains(text, "Followers")
    and contains(text, "Likes")
  if not has_all_counters then return false end

  -- The feed also contains the top tab "Following", the bottom tab
  -- "Profile", and may contain a caption such as "Repost to followers".
  -- Require an owner-profile control, or at least reject the clear feed UI.
  local has_owner_control = contains(text, "Edit profile")
    or contains(text, "Add name")
    or contains(text, "Add bio")
    or contains(text, "Complete your profile")
  local clearly_feed = contains(text, "For You")
    and contains(text, "Home")
    and contains(text, "Friends")
  return has_owner_control or not clearly_feed
end

local function looks_like_login(text)
  return contains(text, "Log in to TikTok")
    or contains(text, "Sign up for TikTok")
    or (contains(text, "Use phone or email") and contains(text, "Log in"))
    or (contains(text, "Create an account") and contains(text, "Continue with"))
end

local function looks_like_tiktok_settings(text)
  return contains(text, "Settings")
    and contains(text, "TikTok")
    and (contains(text, "ALLOW TIKTOK TO ACCESS")
      or contains(text, "Background App Refresh")
      or contains(text, "Allow Tracking"))
end

local function looks_like_feed(text)
  return contains(text, "Home")
    and contains(text, "Profile")
    and (contains(text, "For You")
      or contains(text, "Following")
      or contains(text, "Friends"))
end

local function looks_like_ios_home(text)
  return contains(text, "FaceTime")
    and contains(text, "App Store")
    and (contains(text, "Podcasts")
      or contains(text, "Podcast")
      or contains(text, "Settings")
      or contains(text, "Clock"))
end

local function unsupported_popup(raw, profile_visible)
  local text = normalize(raw)
  local phrases = {
    "Turn on notifications",
    "Enable notifications",
    "Sync contacts",
    "Find contacts",
    "Access your contacts",
    "Add a profile photo",
    "Create a nickname",
    "Allow access",
    "Maybe later",
    "Not now",
    "Got it",
    "Choose what you like",
    "Choose your interests",
  }
  for _, phrase in ipairs(phrases) do
    if contains(text, phrase) then return phrase end
  end

  -- If Profile markers disappear for a sustained period, Windows must capture
  -- the screen instead of this Lua guessing at a close button.
  if not profile_visible and #raw > 40 then return nil end
  return nil
end

pcall(function() screen.init(0) end)

local ok_state, screen_on = pcall(function()
  return device.is_screen_on()
end)

-- LuaAgent 2.3 can report false here even while the physical LCD and snapshot
-- are visibly on. Confirm the framebuffer with fresh OCR before skipping.
-- This remains read-only: no wake command and no tap.
local initial_ocr = ocr()
local visible_characters = #normalize(initial_ocr)
local state_label = ok_state and tostring(screen_on) or "error"
if screen_on == true then
  log("SCREEN_ON_CONFIRMED", "state=true;ocr_chars=" .. tostring(visible_characters))
elseif visible_characters >= 5 then
  log("SCREEN_VISUAL_CONFIRMED",
    "state=" .. state_label ..
    ";ocr_chars=" .. tostring(visible_characters))
else
  log("SKIP_SCREEN_OFF",
    "state=" .. state_label ..
    ";ocr_chars=" .. tostring(visible_characters))
  return
end

local tiktok_bundle = open_tiktok()
if not tiktok_bundle then
  log("APP_NOT_INSTALLED", "TikTok")
  return
end
wait(CFG.launch_wait_ms)

local launch_ocr = ocr()
if looks_like_tiktok_settings(normalize(launch_ocr)) then
  log("SETTINGS_RECOVERY", launch_ocr:sub(1, 450))
  if not reopen_tiktok(tiktok_bundle) then
    log("APP_FOREGROUND_FAILED", "Could not reopen TikTok from Settings")
    return
  end
  wait(CFG.launch_wait_ms)
  local recovered_ocr = ocr()
  if looks_like_tiktok_settings(normalize(recovered_ocr)) then
    log("APP_FOREGROUND_FAILED", recovered_ocr:sub(1, 500))
    return
  end
end

for _ = 1, 5 do
  local ok, handled = handle_known_system_prompt_once()
  if not ok then return end
  if not handled then break end
end

local function enter_or_confirm_profile(allow_reopen)
  local before = ocr()
  log("BEFORE_PROFILE_OCR", before:sub(1, 700))

  local before_normalized = normalize(before)
  local before_header = ocr(20, 100, 710, 540)
  local before_username = parse_username(before_header)
  if looks_like_profile(before_normalized) or before_username then
    -- A previous run or the user may already have Profile open. Do not require
    -- the bottom-tab template in that state and do not tap a second time. The
    -- header username makes this independent of the TikTok display language.
    log("PROFILE_ALREADY_OPEN",
      tostring(before_username or "layout") .. " / " .. before:sub(1, 360))
    return true
  end

  local px, py, profile_image = find_profile_for(9)
  if px then
    tap_found(profile_image, px, py,
      "Profile via " .. profile_image.name, CFG.profile_wait_ms)
    return true
  end

  if looks_like_login(before_normalized) then
    log("NOT_LOGGED_IN", "TikTok profile requires login")
    return false
  end

  -- A photo picker, TikTok settings page, or stale modal may survive app.run.
  -- One clean app restart is safer than guessing a close-button coordinate.
  if allow_reopen then
    log("PROFILE_REOPEN_RETRY", before:sub(1, 420))
    if not reopen_tiktok(tiktok_bundle) then
      log("APP_FOREGROUND_FAILED", "Could not restart TikTok before Profile")
      return false
    end
    wait(CFG.launch_wait_ms)
    for _ = 1, 3 do
      local ok, handled = handle_known_system_prompt_once()
      if not ok then return false end
      if not handled then break end
    end
    return enter_or_confirm_profile(false)
  end

  log("PROFILE_IMAGE_NOT_FOUND", "all profile templates / " .. before:sub(1, 400))
  return false
end

if not enter_or_confirm_profile(true) then return end

local started = os.time()
local candidate_hits = {}
local best_candidate = nil
local best_hits = 0
local consecutive_non_profile = 0
local settings_recovery_attempts = 0
local home_recovery_attempts = 0
local profile_retry_taps = 0
local last_raw = ""

while os.time() - started <= CFG.maximum_profile_observe_seconds do
  local ok, handled = handle_known_system_prompt_once()
  if not ok then return end
  if handled then
    consecutive_non_profile = 0
  else
    local full = ocr()
    -- Username can sit above y=250 on iPhone 6s/7 layouts. Keep this region
    -- limited to the profile header but include the whole top identity block.
    local header = ocr(20, 100, 710, 540)
    last_raw = full
    local normalized = normalize(full)

    local recovered_foreground = false
    if looks_like_tiktok_settings(normalized) then
      if settings_recovery_attempts >= 2 then
        log("APP_FOREGROUND_FAILED", full:sub(1, 600))
        return
      end
      settings_recovery_attempts = settings_recovery_attempts + 1
      log("SETTINGS_RECOVERY",
        "observer_attempt=" .. tostring(settings_recovery_attempts) ..
        " / " .. full:sub(1, 420))
      if not reopen_tiktok(tiktok_bundle) then
        log("APP_FOREGROUND_FAILED", "Could not reopen TikTok from Settings")
        return
      end
      wait(CFG.launch_wait_ms)
      local recovery_x, recovery_y, recovery_image = find_profile_for(5)
      if recovery_x then
        profile_retry_taps = profile_retry_taps + 1
        tap_found(recovery_image, recovery_x, recovery_y,
          "Profile after Settings recovery", 4200)
      end
      consecutive_non_profile = 0
      recovered_foreground = true
    elseif looks_like_ios_home(normalized) then
      if home_recovery_attempts >= 2 then
        log("APP_FOREGROUND_FAILED", "TikTok returned to iOS Home twice")
        return
      end
      home_recovery_attempts = home_recovery_attempts + 1
      log("HOME_RECOVERY",
        "attempt=" .. tostring(home_recovery_attempts) ..
        " / " .. full:sub(1, 420))
      if not reopen_tiktok(tiktok_bundle) then
        log("APP_FOREGROUND_FAILED", "Could not reopen TikTok from iOS Home")
        return
      end
      wait(CFG.launch_wait_ms)
      local recovery_x, recovery_y, recovery_image = find_profile_for(5)
      if recovery_x then
        profile_retry_taps = profile_retry_taps + 1
        tap_found(recovery_image, recovery_x, recovery_y,
          "Profile after Home recovery", 4200)
      end
      consecutive_non_profile = 0
      recovered_foreground = true
    end

    if not recovered_foreground and looks_like_login(normalized) then
      log("NOT_LOGGED_IN", full:sub(1, 450))
      return
    end

    if not recovered_foreground then
      local header_username = parse_username(header)
      local profile_visible = looks_like_profile(normalized) or header_username ~= nil
      local popup = unsupported_popup(full, profile_visible)
      if popup then
        log("POPUP_UNSUPPORTED", popup .. " / " .. full:sub(1, 600))
        return
      end

      if profile_visible then
        consecutive_non_profile = 0
        local username = header_username or parse_username(full)
        if username then
          candidate_hits[username] = (candidate_hits[username] or 0) + 1
          if candidate_hits[username] > best_hits then
            best_candidate = username
            best_hits = candidate_hits[username]
          end
          log("USERNAME_CANDIDATE", username .. " hit=" .. tostring(candidate_hits[username]))
        end
      else
        local retried_profile = false
        if profile_retry_taps < 2 and looks_like_feed(normalized) then
          local px, py, retry_image = find_profile_for(2)
          if px and looks_like_feed(normalize(ocr())) then
            profile_retry_taps = profile_retry_taps + 1
            tap_found(retry_image, px, py,
              "Profile retry " .. tostring(profile_retry_taps), 4200)
            consecutive_non_profile = 0
            retried_profile = true
          end
        end
        if not retried_profile then
          consecutive_non_profile = consecutive_non_profile + 1
          log("PROFILE_STABILIZING", "non_profile=" .. tostring(consecutive_non_profile))
          if consecutive_non_profile >= 6 then
            log("POPUP_UNSUPPORTED", full:sub(1, 800))
            return
          end
        end
      end

      local elapsed = os.time() - started
      if elapsed >= CFG.minimum_profile_observe_seconds
          and best_candidate
          and best_hits >= CFG.required_username_hits then
        -- Final two fresh reads prevent a username from an old VNC/OCR frame.
        wait(900)
        local verify_one = parse_username(ocr(20, 100, 710, 540))
        wait(900)
        local verify_two = parse_username(ocr(20, 100, 710, 540))
        if verify_one == best_candidate and verify_two == best_candidate then
          log("USERNAME_FOUND", best_candidate)
          return
        end
        log("USERNAME_VERIFY_RETRY", tostring(verify_one) .. " / " .. tostring(verify_two))
      end
    end
  end
  collectgarbage("collect")
  wait(CFG.poll_ms)
end

if best_candidate then
  log("USERNAME_NOT_FOUND", "Không đủ xác nhận ổn định cho " .. best_candidate)
else
  log("USERNAME_NOT_FOUND", last_raw:sub(1, 700))
end
