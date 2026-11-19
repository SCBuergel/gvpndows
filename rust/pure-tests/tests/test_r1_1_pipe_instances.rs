//! R1-1: after the first instance the service always holds a listening instance of its pipe, so no
//! other process can recreate the name; client slots are taken and given back exactly once.

use gvpndows_pure_tests::{serve_pipe_loop, PipeServer};

/// Scripted pipe whose client threads close their instance at once (the worst case).
#[derive(Default)]
struct Mock {
    connects: Vec<bool>,
    admits: Vec<bool>,
    creates: Vec<bool>,
    next_id: u32,
    listening: Vec<u32>,
    slots: i32,
    served: u32,
    firsts: Vec<bool>,
    logs: Vec<String>,
}
impl Mock {
    fn check(&self) {
        assert!(!self.listening.is_empty(), "no instance of the service's own exists");
    }
}
impl PipeServer for Mock {
    type H = u32;
    fn create(&mut self, first: bool) -> Result<u32, String> {
        self.firsts.push(first);
        if !self.creates.remove(0) {
            return Err("busy".into());
        }
        self.next_id += 1;
        self.listening.push(self.next_id);
        Ok(self.next_id)
    }
    fn connect(&mut self, h: &u32) -> bool {
        self.check();
        assert!(self.listening.contains(h));
        self.connects.remove(0)
    }
    fn disconnect(&mut self, h: &u32) {
        assert!(self.listening.contains(h));
        self.check();
    }
    fn admit(&mut self) -> bool {
        let ok = self.admits.remove(0);
        self.slots += ok as i32;
        ok
    }
    fn release(&mut self) {
        self.slots -= 1;
        assert!(self.slots >= 0);
    }
    fn serve(&mut self, h: u32) {
        self.listening.retain(|x| *x != h); // the client thread closes it now
        self.check();
        self.slots -= 1;
        self.served += 1;
    }
    fn log(&mut self, msg: String) {
        self.logs.push(msg);
    }
    fn pause(&mut self) {}
    fn running(&mut self) -> bool {
        !self.connects.is_empty()
    }
}

#[test]
fn an_instance_always_exists_and_slots_balance() {
    let mut m = Mock {
        // ok, client gone, ok but at the cap, ok but the next create fails, ok
        connects: vec![true, false, true, true, true],
        admits: vec![true, false, true, true],
        creates: vec![true, true, false, true],
        ..Default::default()
    };
    serve_pipe_loop(&mut m);
    assert_eq!(m.firsts, [true, false, false, false]);
    assert_eq!(m.served, 2);
    assert_eq!(m.slots, 0);
    assert_eq!(m.listening.len(), 1);
    assert_eq!(m.logs.len(), 1);
    assert!(m.logs[0].contains("takeover"));
}

#[test]
fn first_create_failing_stops_serving() {
    let mut m = Mock { connects: vec![true], creates: vec![false], ..Default::default() };
    serve_pipe_loop(&mut m);
    assert_eq!(m.firsts, [true]);
    assert_eq!(m.connects.len(), 1);
    assert!(m.logs[0].contains("not serving"));
}
