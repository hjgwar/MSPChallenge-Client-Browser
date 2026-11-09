import Shepherd from 'https://cdn.jsdelivr.net/npm/shepherd.js/dist/esm/shepherd.mjs';

function addPartAnnouncement(tour, id, title, text, prevPartId, nextPartId) {
    const buttonsVar = [
        {
            action() {
                return this.back();
            },
            classes: 'shepherd-backbutton',
            text: '&lt;'
        },
        {
            action() {
                return this.next();
            },
            classes: 'shepherd-nextbutton',
            text: '&gt;'
        }
    ]
    if (prevPartId) {
        buttonsVar.push({
            action() {
                return this.show(prevPartId);
            },
            classes: 'shepherd-prevpartbutton',
            text: 'Back to previous part'
        });
    }
    if (nextPartId) {
        buttonsVar.push({
            action() {
                return this.show(nextPartId);
            },
            classes: 'shepherd-nextpartbutton',
            text: 'Skip to next part'
        });
    }
    tour.addStep({
        id: id,
        title: title,
        classes: 'shepherd-fullscreen',
        text: text,
        buttons: buttonsVar
    });
}

export function initiateTutorial() {
    const tour = new Shepherd.Tour({
        defaultStepOptions: {
            cancelIcon: {
                enabled: false
            },
            scrollTo: { behavior: 'smooth', block: 'center' }
        }
    });

    tour.addStep({
        title: 'Welcome',
        classes: 'shepherd-fullscreen',
        text: 'We recommend you follow this tutorial to get to know the basic features of MSP Challenge.',
        buttons: [
            {
                action() {
                    return this.cancel();
                },
                text: 'Cancel'
            },
            {
                action() {
                    return this.next();
                },
                text: 'OK'
            }
        ],
        id: 'welcome'
    });

    addPartAnnouncement(
        tour,
        'getstarted',
        'Let\'s get started!',
        'You are now a marine and maritime spatial planner! Your job is to create, implement and evaluate spatial plans for this area. You will be working together with other players logged on to this session.',
        '',
        ''
    );

    addPartAnnouncement(
        tour,
        'part1',
        'Part 1: Interface Basics',
        'Before we start creating, implementing and evaluating spatial plans, let\'s walk through some basics.',
        '',
        'part2'
    );

    tour.addStep({
        title: 'Interface Basics [1/6]',
        classes: 'shepherd-bottomscreen',
        text: 'During this session you will govern the country associated with the colour you picked when logging in.',
        buttons: [
            {
                action() {
                    return this.back();
                },
                text: '&lt;'
            },
            {
                action() {
                    return this.show('part2');
                },
                text: 'Skip to Part 2'
            },
            {
                action() {
                    return this.next();
                },
                text: '&gt;'
            }
        ],
        id: 'interfacebasics1'
    });

    addPartAnnouncement(
        tour,
        'part2',
        'Part 2: Accessing Data & Exploring Consequences',
        'Before you can start creating spatial plans, you first need to know what is already going on in this area of the ocean.',
        'part1',
        'part3'
    );

    tour.start();
}