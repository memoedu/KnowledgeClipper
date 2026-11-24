use libmathcat::interface;
use std::cell::RefCell;

// The JS adapter uses one synchronous call at a time. Keep the result alive until its next call.
thread_local! {
    static INPUT: RefCell<Vec<u8>> = const { RefCell::new(Vec::new()) };
    static OUTPUT: RefCell<String> = const { RefCell::new(String::new()) };
    static LANGUAGE: RefCell<Option<bool>> = const { RefCell::new(None) };
}

#[no_mangle]
pub extern "C" fn input_buffer(size: usize) -> *mut u8 {
    INPUT.with(|buffer| {
        let mut value = buffer.borrow_mut();
        value.resize(size, 0);
        value.as_mut_ptr()
    })
}

#[no_mangle]
pub extern "C" fn read_math(chinese: bool) -> usize {
    let result = INPUT.with(|buf| -> Option<String> {
        let bytes = buf.borrow();
        let mathml = std::str::from_utf8(&bytes).ok()?;
        LANGUAGE.with(|stored| -> Option<()> {
            let previous = *stored.borrow();
            if previous.is_none() { interface::set_rules_dir("Rules").ok()?; }
            if previous != Some(chinese) {
                interface::set_preference("Language", if chinese { "zh-tw" } else { "en" }).ok()?;
                interface::set_preference("SpeechStyle", "ClearSpeak").ok()?;
                *stored.borrow_mut() = Some(chinese);
            }
            Some(())
        })?;
        interface::set_mathml(mathml).ok()?;
        interface::get_spoken_text().ok()
    });
    OUTPUT.with(|out| {
        let mut value = out.borrow_mut();
        *value = result.unwrap_or_default();
        value.len()
    })
}

#[no_mangle]
pub extern "C" fn output_buffer() -> *const u8 { OUTPUT.with(|out| out.borrow().as_ptr()) }
